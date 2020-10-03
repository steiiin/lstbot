using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

public class Bot
{

    public async Task StartBot()
    {

        //Einstellungen laden
        GetUserSettings();

        Print.Welcome();
        a = new API(UserSettings[SETTING_LOGIN_USER], UserSettings[SETTING_LOGIN_PASS]);

        //Login
        bool loginOk = false;
        while (!loginOk)
        {
            Print.StartTask(0, "Bei Leitstellenspiel.de anmelden", Print.LogLevel.LOG_HIGH);
            loginOk = await a.Login();
            if (!loginOk)
            {
                Print.FinishTask(Print.FinishTaskResult.FEHLER);
                Print.Info(0, "Erneuter Versuch in 1min", Print.LogLevel.LOG_HIGH);
                System.Threading.Thread.Sleep(60 * 1000);
            }
        }
        Print.FinishTask(Print.FinishTaskResult.OK);

        //Initialisieren
        startTime = DateTime.Now;
        int loopCounter = 1;
        ActiveMissions = new Dictionary<string, Mission>();
        ActiveVehicles = new Dictionary<string, Vehicle>();

        //Schleife starten
        DateTime nextDoWorkersUpdate = DateTime.Now;
        do
        {

            try
            {

                //Ausgabe
                Print.NewLoop(loopCounter, (DateTime.Now - startTime));

                //Personal überprüfen
                Print.StartTask(0, "Personal überprüfen", Print.LogLevel.LOG_LOW);
                if (DateTime.Now >= nextDoWorkersUpdate)
                {
                    await DoWorkersLoop();
                    nextDoWorkersUpdate = DateTime.Now.AddDays(1);
                    Print.Info(2, "Nächste Überprüfung: " + nextDoWorkersUpdate.ToString(), Print.LogLevel.LOG_LOW);
                }
                else { Print.AwaitTask("Geplant > " + nextDoWorkersUpdate.ToString()); Print.FinishTask(Print.FinishTaskResult.ABBRUCH); }

                //MissionsSchleife
                await DoMissionsLoop();

            }
            catch (Exception e)
            {
                Print.Error("[GLOBAL] / Unbekannter Fehler in StartBot aufgetreten: ", e.Message + e.StackTrace);
                System.Threading.Thread.Sleep(TimeSpan.FromMinutes(1));
            }

            //Pausieren
            Print.EndLoop(TimeSpan.FromMinutes(1));
            loopCounter += 1;

        } while (true);
               
    }
    public async Task Reset()
    {

        //Einstellungen laden
        GetUserSettings();

        try
        {
            Print.Info(0, "Reset. Alle Einsätze werden zurückgerufen.", Print.LogLevel.LOG_LOW);
            a = new API(UserSettings[SETTING_LOGIN_USER], UserSettings[SETTING_LOGIN_PASS]);

            //Login
            bool loginOk = false;
            while (!loginOk)
            {
                Print.StartTask(0, "Bei Leitstellenspiel.de anmelden", Print.LogLevel.LOG_LOW);
                loginOk = await a.Login();
                if (!loginOk)
                {
                    Print.FinishTask(Print.FinishTaskResult.FEHLER);
                    Print.Info(0, "Erneuter Versuch in 1min", Print.LogLevel.LOG_LOW);
                    System.Threading.Thread.Sleep(60 * 1000);
                }
            }
            Print.FinishTask(Print.FinishTaskResult.OK);

            //Schleife starten
            var missionsArgs = await a.GetMissions(); if (missionsArgs.IsEmpty) { Print.FinishTask(Print.FinishTaskResult.FEHLER); }
            else
            {

                foreach (var m in missionsArgs.Missions)
                {
                    Print.StartTask(2, "Einsatz [" + m.Title + "] zurücksetzen.", Print.LogLevel.LOG_LOW);
                    if (await a.InvokeResetMission(m.ID)) { Print.FinishTask(Print.FinishTaskResult.OK); } else { Print.FinishTask(Print.FinishTaskResult.FEHLER); }
                }

            }

        }
        catch (Exception e)
        {
            Print.Error("[GLOBAL] / Unbekannter Fehler in StartBot aufgetreten: ", e.Message + e.StackTrace);
        }

    }

    #region Bot

    private async Task DoMissionsLoop()
    {

        //Neue Einsätze erstellen
        Print.StartTask(0, "Neue Einsätze erstellen", Print.LogLevel.LOG_HIGH);
        if (await a.InvokeMissionGeneration()) { Print.FinishTask(Print.FinishTaskResult.OK); } else { Print.FinishTask(Print.FinishTaskResult.FEHLER); }

        var canceled = new List<VehicleClass>();

        //Einsatz-IDs laden
        Print.StartTask(0, "Einsatz-IDs laden", Print.LogLevel.LOG_LOW);
        var missionsArgs = await a.GetMissions(); if (missionsArgs.IsEmpty) { Print.FinishTask(Print.FinishTaskResult.FEHLER); }
        else
        {

            Print.FinishTask(Print.FinishTaskResult.OK);

            //Credits berechnen
            currentCredits = missionsArgs.Credits;
            if (lastCredits > currentCredits || startCredits == 0)
            {
                startCredits = currentCredits;
                startTime = DateTime.Now;
            }
            else
            {
                double more = currentCredits - startCredits;
                averageCredits = (long)((more / (DateTime.Now - startTime).TotalHours) * 24);
            }
            lastCredits = currentCredits;

            //Einsätze durchlaufen
            List<string> toDeleteMissionIDs = ActiveMissions.Keys.ToList();
            int index = 0;

            foreach (var m in missionsArgs.Missions)
            {

                index += 1;
                string indexText = "[" + index + "/" + missionsArgs.Missions.Count + "]";

                Print.StartTask(1, "Einsatz [" + m.ID + "]" + indexText + " - " + m.Title + " abrufen", Print.LogLevel.LOG_LOW);
                var detailArgs = await a.GetMissionDetail(m.ID, m.Category); if (detailArgs == null) { Print.FinishTask(Print.FinishTaskResult.FEHLER); }
                else
                {

                    //Einsatz aktualisieren
                    m.SetDetail(detailArgs);
                    Print.FinishTask(Print.FinishTaskResult.OK);
                    if (m.State != MissionState.BEENDET)
                    {
                        toDeleteMissionIDs.Remove(m.ID);

                        //Speicher aktualisieren
                        UpdateBackup(m);
                    }

                    //Fahrzeuge organisieren
                    if (m.State != MissionState.BEENDET) { 
                        if (await HandleRD_Talk(m) ||
                            await HandleRD_NoPat(m) ||
                            await HandlePOL_Cell(m))
                    {

                        //Einsatz aktualisieren
                        var update = await a.GetMissionDetail(m.ID, m.Category);
                        if (!update.IsEmpty && update.Missing.IsEmpty && update.Patients.Count == 0) { update = new API.GetMissionDetailArgs(); }
                        m.SetDetail(update);

                    }
                    }
                    
                    //Mission bearbeiten
                    var args = await HandleMission(m, canceled);
                    canceled.AddRange(args.CanceledVehicles);

                    //Statistik
                    UpdateMissionStats(m);
                    
                }

            }

            //Abgeschlossene Einsätze löschen
            foreach (var missionId in toDeleteMissionIDs) { ActiveMissions.Remove(missionId); }
            finishedCounter += toDeleteMissionIDs.Count;

            //Übersicht anzeigen
            Print.Overview(ActiveMissions.Values.ToList(), stats_mission, (DateTime.Now - startTime), finishedCounter);

        }

        //Aufhängen vermeiden
        int activeCount = (from x in ActiveMissions.Values where x.State == MissionState.IM_EINSATZ || x.State == MissionState.WARTE_AUF_EINTREFFEN || x.State == MissionState.BEENDET || x.State == MissionState.GEPLANT_VORORT select x).Count();
        if (activeCount == 0)
        {
            Print.Error("DoLoop/ActiveCount", "Die Einsätze können nicht bearbeitet werden.");
            await Reset();
        }

    }

    private async Task DoWorkersLoop()
    {

        var buildingsArgs = await a.GetBuildings(); if (buildingsArgs.IsEmpty) { Print.FinishTask(Print.FinishTaskResult.FEHLER); }
        else
        {

            Print.FinishTask(Print.FinishTaskResult.OK);

            //Gebäude durchlaufen
            if (buildingsArgs.MissingPersonal.Count == 0) { Print.Info(1, "Kein Personalmangel.", Print.LogLevel.LOG_LOW); }
            else
            {
                Print.Info(1, "Personal anwerben:", Print.LogLevel.LOG_LOW);
                foreach (var b in buildingsArgs.MissingPersonal)
                {

                    Print.StartTask(3, "[" + b.Title + "] fehlt Personal", Print.LogLevel.LOG_HIGH);
                    int demand = b.PersonalTarget - b.PersonalCurrent;
                    Print.AwaitTask(demand.ToString() + " Mann");

                    Print.AwaitTask("anwerben");
                    if (await a.InvokeDoHire(b.ID, demand))
                    {
                        Print.FinishTask(Print.FinishTaskResult.OK);
                    }
                    else
                    {
                        Print.FinishTask(Print.FinishTaskResult.FEHLER);
                    }

                }
            }

        }

    }

    //#########################################################################################

    private void UpdateBackup(Mission m)
    {
        if (ActiveMissions.ContainsKey(m.ID))
        {
            ActiveMissions[m.ID].Merge(m);
        }
        else
        {
            ActiveMissions.Add(m.ID, m);
        }

        foreach (var v in m.LinkedVehicles.Values)
        {
            if (ActiveVehicles.ContainsKey(v.ID))
            {
                ActiveVehicles[v.ID].Merge(v);
            }
            else
            {
                ActiveVehicles.Add(v.ID, v);
            }
        }
    }

    //#########################################################################################

    private struct GetHandleMissionArgs
    {

        public List<VehicleClass> CanceledVehicles { get; }

        public GetHandleMissionArgs(List<VehicleClass> canceledVehicles)
        {
            CanceledVehicles = canceledVehicles;
        }

    }

    private async Task<GetHandleMissionArgs> HandleMission(Mission m, List<VehicleClass> canceled)
    {

        var addCancel = new List<VehicleClass>();

        //Neuer Einsatz
        if (m.State == MissionState.NEU)
        {
            Print.MissionState(m);

            //Verfügbare Fahrzeuge suchen, außer Anhänger
            var available = from x in m.AvailableVehicles where !x.IsTrailer && !x.IsTractor select x;

            //Keine Alarmierung, wenn keine Fahrzeuge vorhanden
            if (available.Count() == 0)
            {
                Print.Info(2, "Keine Fahrzeuge zur Probealarmierung vorhanden. [ABBRUCH]", Print.LogLevel.LOG_HIGH);
                return new GetHandleMissionArgs(addCancel);
            }

            //Fahrzeug zur Probe alarmieren
            var test = available.First();

            Print.StartTask(2, "Fahrzeug zur Probe alarmieren. [" + test.Title + "]", Print.LogLevel.LOG_HIGH);
            if (await a.InvokeTestMission(m.ID, test.ID))
            {
                Print.FinishTask(Print.FinishTaskResult.OK);
                m.SetState(MissionState.RESET);
            }
            else
            {
                Print.FinishTask(Print.FinishTaskResult.FEHLER);
                return new GetHandleMissionArgs(addCancel);
            }
        }

        //Fahrzeuge zrückrufen
        if (m.State == MissionState.RESET)
        {
            Print.MissionState(m);
            Print.StartTask(2, "Fahrzeuge zurückrufen", Print.LogLevel.LOG_HIGH);

            int boundCount = m.VehicleAlertedCount + m.VehicleArrivedCount;
            if (await a.InvokeResetMission(m.ID))
            {
                Print.AwaitTask(boundCount + 1); //Server lässt knapp 1s pro Statusänderung vergehen
                var update = await a.GetMissionDetail(m.ID, m.Category);

                m.SetDetail(update);
                m.SetState(MissionState.WARTE_AUF_DISPONIERUNG);

                Print.FinishTask(Print.FinishTaskResult.OK);
            }
            else
            {
                Print.FinishTask(Print.FinishTaskResult.FEHLER);
                return new GetHandleMissionArgs(addCancel);
            }
        }

        //Alarmierung
        if (m.State == MissionState.WARTE_AUF_DISPONIERUNG && !m.Missing.IsEmpty)
        {
            Print.MissionState(m);

            bool alarmCanceled = false;
            var alarmObj = new VehicleAlert();

            //Status ausgeben
            Print.MissionMissing(m, ref canceled, ref addCancel, ref alarmCanceled);
            if (alarmCanceled)
            {
                Print.Info(3, "[EINSATZ ÜBERSPRUNGEN]", Print.LogLevel.LOG_HIGH, ConsoleColor.Gray);
                return new GetHandleMissionArgs(addCancel);
            }
            
            //Fahrzeuge zusammenstellen
            Print.Info(2, "Fahrzeuge zusammenstellen", Print.LogLevel.LOG_HIGH);
            
            if (m.AvailableVehicles.Count() == 0)
            {
                Print.Info(4, "Keine Fahrzeuge für diesen Einsatz verfügbar", Print.LogLevel.LOG_HIGH);
                alarmCanceled = true;
            }
            if (m.Missing.MissingClasses.Count > 0 && !alarmCanceled)
            {

                //Fahrzeugklassen vorhanden
                if ((int)m.Missing.MissingClasses.First().Key < 1000)
                { Print.Info(3, "Nach Fahrzeugklasse ...", Print.LogLevel.LOG_HIGH); }

                for (int i = 0; i < m.Missing.MissingClasses.Keys.Count; i++)
                {
                    VehicleClass vClass = m.Missing.MissingClasses.Keys.ElementAt(i);
                    int vDemand = m.Missing.MissingClasses[vClass];
                    var vClassText = vClass.ToString() + Print.GetIntentSpacing(vClass.ToString());

                    #region Fahrzeugklassen

                    if ((int)vClass < 1000)
                    {

                        //Wenn auf der Verbotsliste, dann abbrechen
                        if (canceled.Contains(vClass))
                        {

                            Print.Info(4, "Benötigtes Fahrzeug bereits in anderem Einsatz benötigt: " + vClass.ToString(), Print.LogLevel.LOG_HIGH);
                            addCancel.Add(vClass);
                            alarmCanceled = true;
                            break;
                        
                        }
                        else
                        {

                            var cars = Vehicle.Get(m.AvailableVehicles, alarmObj.ToAlert, vClass);

                            //Abbruch, wenn zu wenig verfügbare Fahrzeuge > AUßER: Klasse auf Flexibel-Liste
                            if ((cars.Count() < vDemand))
                            {

                                addCancel.Add(vClass);

                                if (m.Missing.FlexibleDemand.Contains(vClass) && (cars.Count() > 0))
                                {

                                    Print.StartTask(4, "Nicht genug verfügbare Fahrzeuge vorhanden", Print.LogLevel.LOG_HIGH);
                                    Print.AwaitTask(vDemand.ToString() + "x " + vClass.ToString());
                                    Print.FinishTask(Print.FinishTaskResult.OK);
                                    Print.Info(4, vClass.ToString() + " darf flexibel alarmiert werden.", Print.LogLevel.LOG_HIGH);

                                }
                                else
                                {

                                    Print.StartTask(4, "Nicht genug verfügbare Fahrzeuge vorhanden", Print.LogLevel.LOG_HIGH);
                                    Print.AwaitTask(vDemand.ToString() + "x " + vClass.ToString());
                                    Print.FinishTask(Print.FinishTaskResult.ABBRUCH);

                                    alarmCanceled = true;
                                    break;

                                }

                            }

                            //Zur Alarmierungsliste hinzufügen
                            for (int j = 0; j < vDemand; j++)
                            {

                                if (cars.Count <= j) { break; }
                                var selected = cars.ElementAt(j);

                                //Abrollcontainer & Anhänger nur schicken, wenn Zugfahrzeug vorhanden
                                if (selected.IsTrailer)
                                {

                                    //Anhänger alarmieren & alle Zugmaschinen auf Mode=2 setzen
                                    if (selected.IsTractorAvailable)
                                    {

                                        var tractors = from x in m.AvailableVehicles where x.IsTractor && x.IsAvailableAtStation select x;
                                        foreach (var t in tractors) { alarmObj.AddTractor(t.ID); }

                                        string tractorTitle = "zufälliger Zugmaschine";
                                        if(selected.IsLinkedTrailer && ActiveVehicles.ContainsKey(selected.LinkedTractorID)) 
                                        {
                                            var linked = ActiveVehicles[selected.LinkedTractorID];
                                            tractorTitle = linked.Title;
                                            m.Missing.ReduceDemand(linked);
                                        }

                                        Print.Info(4, vClassText + " [" + selected.Title + "] mit [" + tractorTitle + "]", Print.LogLevel.LOG_HIGH);
                                        alarmObj.AddVehicle(selected.ID);

                                    }
                                    else
                                    {
                                        Print.Info(4, vClass.ToString() + " [" + selected.Title + "] hat keine Zugmaschine.", Print.LogLevel.LOG_HIGH);
                                        alarmCanceled = true;
                                        break;
                                    }

                                }
                                else if (selected.IsTractor)
                                {

                                    //Zugmaschine zusammen mit einem festen Anhänger alarmieren
                                    var linkedTrailer = from x in m.AvailableVehicles where x.IsTrailer && x.LinkedTractorID == selected.ID select x;
                                    if (linkedTrailer.Count() == 1)
                                    {

                                        alarmObj.AddVehicle(linkedTrailer.First().ID);
                                        alarmObj.AddTractor(selected.ID);

                                    }
                                    else if (linkedTrailer.Count() == 0)
                                    {

                                        alarmObj.AddVehicle(selected.ID);

                                    }
                                    else
                                    {

                                        alarmObj.AddVehicle(selected.ID);
                                        Print.Error("HandleMission/#region 'Nach Fahrzeugtyp'/IsTractor", "Mehr als einen Verlinkten Trailer gefunden??");

                                    }

                                }
                                else
                                {

                                    Print.Info(4, vClassText + " [" + selected.Title + "]", Print.LogLevel.LOG_HIGH);
                                    alarmObj.AddVehicle(selected.ID);

                                }

                                //Bedarf abziehen
                                m.Missing.ReduceDemand(selected);

                            }

                        }

                    }

                    #endregion

                    #region Prozedurklassen: Feuerwehr

                    else if (vClass == VehicleClass.PROC_FW_MEN)
                    {
                        
                        if (vDemand == 1)
                        {
                            Print.Info(3, "Nach Mannstärke (1 Feuerwehrmann) ...", Print.LogLevel.LOG_HIGH);
                        }
                        else
                        {
                            Print.Info(3, "Nach Mannstärke (" + vDemand.ToString() + " Feuerwehrleute) ...", Print.LogLevel.LOG_HIGH);
                        }

                        //Fahrzeuge [LF, MTW, RW] wählen, die noch nicht alarmiert wurden & dann nach Typ (MTW zuerst), dann Mannstärke wählen
                        var cars = from x in m.AvailableVehicles where (x.Type == VehicleType.FW_LF || x.Type == VehicleType.FW_MTW || x.Type == VehicleType.FW_HLF || x.Type == VehicleType.FW_Rüst) && !alarmObj.Contains(x.ID)
                                   orderby x.Type descending, x.MenAmount descending
                                   select x;

                        List<string> toAlert = new List<string>();
                        for (int j = 0; j < cars.Count(); j++)
                        {

                            var selected = cars.ElementAt(j);
                            vDemand -= selected.MenAmount;

                            string menText = selected.MenAmount.ToString() + " Mann auf ";
                            Print.Info(4, menText + Print.GetIntentSpacing(menText) + " [" + selected.Title + "]", Print.LogLevel.LOG_HIGH);
                            toAlert.Add(selected.ID);

                            if(vDemand <= 0) { break; }

                        }

                        //Fahrzeuge nur alarmieren, wenn genug Mann aquiriert wurden
                        if (vDemand <= 0)
                        {

                            foreach (var id in toAlert)
                            {
                                alarmObj.AddVehicle(id);
                            }

                        }
                        else 
                        {

                            Print.Info(3, "Nicht genug Fahrzeuge vorhanden.", Print.LogLevel.LOG_HIGH);
                            Print.Info(3, "Es fehlen " + vDemand.ToString() + " Mann.", Print.LogLevel.LOG_HIGH);
                            alarmCanceled = true;
                            break;

                        }

                    }

                    else if (vClass == VehicleClass.PROC_FW_WATER)
                    {

                        Print.Info(3, "Nach Löschwasserbedarf (" + vDemand.ToString() + "l) ...", Print.LogLevel.LOG_HIGH);

                        if (canceled.Contains(VehicleClass.FW_LF))
                        {
                            Print.StartTask(4, "[FW_LF] bereits in anderem Einsatz benötigt.", Print.LogLevel.LOG_HIGH);
                            Print.FinishTask(Print.FinishTaskResult.ABBRUCH);
                            alarmCanceled = true;
                        }
                        else
                        {

                            //Fahrzeuge [LF] wählen
                            var cars = from x in m.AvailableVehicles
                                       where (x.Type == VehicleType.FW_LF || x.Type == VehicleType.FW_HLF) && !alarmObj.Contains(x.ID)
                                       orderby x.WaterAmount descending select x;

                            List<string> toAlert = new List<string>();
                            for (int j = 0; j < cars.Count(); j++)
                            {

                                var selected = cars.ElementAt(j);
                                vDemand -= selected.WaterAmount;

                                string waterText = selected.WaterAmount.ToString() + "l auf ";
                                Print.Info(4, waterText + Print.GetIntentSpacing(waterText) + " [" + selected.Title + "]", Print.LogLevel.LOG_HIGH);
                                toAlert.Add(selected.ID);

                                if(vDemand <= 0) { break; }

                            }

                            //Fahrzeuge nur alarmieren, wenn genug Mann aquiriert wurden
                            if (vDemand <= 0)
                            {

                                foreach (var id in toAlert)
                                {
                                    alarmObj.AddVehicle(id);
                                }

                            }
                            else
                            {

                                Print.Info(3, "Nicht genug Fahrzeuge vorhanden.", Print.LogLevel.LOG_HIGH);
                                Print.Info(3, "Es fehlen " + vDemand.ToString() + "l Löschwasser.", Print.LogLevel.LOG_HIGH);
                                alarmCanceled = true;
                                break;

                            }

                        }

                    }

                    #endregion
                    #region Prozedurklassen: Polizei

                    else if (vClass == VehicleClass.PROC_POL_MEN)
                    {

                        Print.Info(3, "Nach Mannstärke (" + vDemand.ToString() + " Polizisten) ...", Print.LogLevel.LOG_HIGH);

                        //Fahrzeuge [FuStW] wählen & nach Mannstärke sortieren
                        var cars = from x in m.AvailableVehicles
                                   where (x.Type == VehicleType.POL_FuStW) && !alarmObj.Contains(x.ID)
                                   orderby x.MenAmount descending
                                   select x;

                        List<string> toAlert = new List<string>();
                        for (int j = 0; j < cars.Count(); j++)
                        {

                            var selected = cars.ElementAt(j);
                            vDemand -= selected.MenAmount;

                            string menText = selected.MenAmount.ToString() + " Mann auf ";
                            Print.Info(4, menText + Print.GetIntentSpacing(menText) + " [" + selected.Title + "]", Print.LogLevel.LOG_HIGH);
                            toAlert.Add(selected.ID);
                            
                            if(vDemand <= 0) { break; }
                        }

                        //Fahrzeuge nur alarmieren, wenn genug Mann aquiriert wurden
                        if (vDemand <= 0)
                        {

                            foreach (var id in toAlert)
                            {
                                alarmObj.AddVehicle(id);
                            }

                        }
                        else
                        {

                            Print.Info(3, "Nicht genug Fahrzeuge vorhanden.", Print.LogLevel.LOG_HIGH);
                            Print.Info(3, "Es fehlen " + vDemand.ToString() + " Mann.", Print.LogLevel.LOG_HIGH);
                            alarmCanceled = true;
                            break;

                        }

                    }

                    #endregion
                    #region Prozedurklassen: Wasserrettung

                    else if (vClass == VehicleClass.PROC_WR_MEN)
                    {

                        Print.Info(3, "Nach Mannstärke (" + vDemand.ToString() + " Mann mit Ausbildung Wasserrettung) ...", Print.LogLevel.LOG_HIGH);

                        //Fahrzeuge [GW-Wasserrettung] wählen, die noch nicht alarmiert wurden & dann nach Typ (MTW zuerst), dann Mannstärke wählen
                        var cars = from x in m.AvailableVehicles
                                   where (x.Type == VehicleType.WR_GW_Wasserrettung) && !alarmObj.Contains(x.ID)
                                   orderby x.MenAmount descending
                                   select x;

                        List<string> toAlert = new List<string>();
                        for (int j = 0; j < cars.Count(); j++)
                        {

                            var selected = cars.ElementAt(j);
                            vDemand -= selected.MenAmount;

                            string menText = selected.MenAmount.ToString() + " Mann auf ";
                            Print.Info(4, menText + Print.GetIntentSpacing(menText) + " [" + selected.Title + "]", Print.LogLevel.LOG_HIGH);
                            toAlert.Add(selected.ID);

                            if (vDemand <= 0) { break; }

                        }

                        //Fahrzeuge nur alarmieren, wenn genug Mann aquiriert wurden
                        if (vDemand <= 0)
                        {

                            foreach (var id in toAlert)
                            {
                                alarmObj.AddVehicle(id);
                            }

                        }
                        else
                        {

                            Print.Info(3, "Nicht genug Fahrzeuge vorhanden.", Print.LogLevel.LOG_HIGH);
                            Print.Info(3, "Es fehlen " + vDemand.ToString() + " Mann mit Ausbildung Wasserrettung", Print.LogLevel.LOG_HIGH);
                            alarmCanceled = true;
                            break;

                        }

                    }

                    #endregion

                }

            }

            //Alarmierung
            if (alarmCanceled)
            {
                Print.Info(3, "[EINSATZ NICHT DISPONIERT]", Print.LogLevel.LOG_HIGH, ConsoleColor.Red);
                return new GetHandleMissionArgs(addCancel);
            }
            else
            {

                Print.StartTask(3, "Ausrückliste alarmieren", Print.LogLevel.LOG_HIGH);

                string code = await a.InvokeAlarm(m.ID, alarmObj);
                if ( string.IsNullOrEmpty(code))
                {
                    int wait = alarmObj.ToAlert.Count;
                    if (wait > 3) { wait = 3; }
                    Print.AwaitTask(wait);

                    var update = await a.GetMissionDetail(m.ID, m.Category);
                    m.SetDetail(update);

                    Print.FinishTask(Print.FinishTaskResult.OK);
                    m.SetState(MissionState.WARTE_AUF_EINTREFFEN);

                    Print.Info(3, "[EINSATZ ERFOLGREICH DISPONIERT]", Print.LogLevel.LOG_HIGH, ConsoleColor.Green);
                }
                else
                {
                    m.SetState(MissionState.WARTE_AUF_DISPONIERUNG);
                    Print.FinishTask(Print.FinishTaskResult.FEHLER);
                    Print.Info(3, "[EINSATZ NICHT DISPONIERT]", Print.LogLevel.LOG_HIGH, ConsoleColor.Red);
                    Print.Error("HandleMission/Disponierung", code);
                }
            }

        }

        //Warte-Status
        Print.MissionState(m);
        return new GetHandleMissionArgs(addCancel);

    }

    //#########################################################################################

    private async Task<bool> HandleRD_Talk(Mission m)
    {

        if(m.LinkedVehicles == null) { return false; }

        var source = from x in m.LinkedVehicles.Values where (x.Type == VehicleType.RD_RTW || x.Type == VehicleType.RD_RTH || x.Type == VehicleType.RD_NAW) && x.FMS == 5 select x;
        var talk = from x in source where x.FMS == 5 select x;

        if (talk.Count() > 0)
        {
            Print.Info(2, "Sprechwunsch [RTW/RTH/NAW] beantworten", Print.LogLevel.LOG_HIGH);
            foreach (var v in talk)
            {
                Print.StartTask(4, "[Patient] in " + v.Title, Print.LogLevel.LOG_HIGH);
                var x = await a.GetHospitalOptions(v.ID);

                if (x.IsEmpty)
                {
                    Print.AwaitTask("Kein Bett");
                    Print.AwaitTask("wird entlassen");

                    if (await a.InvokeFreePatient(v.ID))
                    {
                        Print.FinishTask(Print.FinishTaskResult.OK);
                    }
                    else
                    {
                        Print.FinishTask(Print.FinishTaskResult.FEHLER);
                    }
                }
                else
                {
                    Hospital h = x.SuitableHospital;
                    Print.AwaitTask(h.Title);

                    if (await a.InvokeTransportPatient(v.ID, h.ID))
                    {

                        v.SetFMS(7);
                        Print.AwaitTask(1);
                        Print.FinishTask(Print.FinishTaskResult.OK);
                    }
                    else
                    {
                        Print.FinishTask(Print.FinishTaskResult.FEHLER);
                    }
                }

            }
            return true;
        }

        return false;
    }

    private async Task<bool> HandleRD_NoPat(Mission m)
    {

        //Nur bei normalen Einsätze
        if (m.Category != MissionCategory.NORMAL || m.LinkedVehicles == null) { return false; }

        //Rettungsfahrzeuge ohne Patienten
        var source = from x in m.LinkedVehicles.Values
                     where ((x.Type == VehicleType.RD_RTW ||
                             x.Type == VehicleType.RD_NEF ||
                             x.Type == VehicleType.RD_KTW ||
                             x.Type == VehicleType.RD_NAW ||
                             x.Type == VehicleType.RD_RTH ||
                             x.Type == VehicleType.RD_GRTW) &&
                             x.FMS == 4 &&
                             ActiveMissions.ContainsKey(x.AssignedMissionID) &&
                             !x.HasPatient &&
                             ActiveMissions[x.AssignedMissionID].State == MissionState.IM_EINSATZ) ||
                           ((x.Type == VehicleType.RD_LNA ||
                             x.Type == VehicleType.RD_OrgL ||
                             x.Type == VehicleType.RD_ELW_SEG ||
                             x.Type == VehicleType.RD_GW_SAN) &&
                             x.FMS == 4 &&
                             ActiveMissions.ContainsKey(x.AssignedMissionID) &&
                             m.Patients.Count == 0 &&
                             ActiveMissions[x.AssignedMissionID].State == MissionState.IM_EINSATZ)
                     select x;

        if (source.Count() > 0)
        {

            Print.Info(2, "[Rettungsdienstfahrzeuge] ohne Einsätze abrücken lassen", Print.LogLevel.LOG_HIGH);
            foreach (var v in source)
            {
                Print.StartTask(4, v.Title + " in " + ActiveMissions[v.AssignedMissionID].Title + " hat keine Patienten", Print.LogLevel.LOG_HIGH);
                Print.AwaitTask("abrücken");

                if (await a.InvokeResetVehicle(v.ID))
                {
                    Print.FinishTask(Print.FinishTaskResult.OK);
                }
                else
                {
                    Print.FinishTask(Print.FinishTaskResult.FEHLER);
                }
            }
            return true;

        }

        return false;

    }

    private async Task<bool> HandlePOL_Cell(Mission m)
    {

        if(m.LinkedVehicles == null) { return false; }

        if (m.Missing.NeedCell)
        {

            int count = m.Missing.MissingClasses[VehicleClass.PROC_POL_PRISONERS];
            Print.Info(2, "Gefangene in [" + m.Title + "] transportieren (" + count.ToString() + " Mann) ...", Print.LogLevel.LOG_HIGH);

            var cars = from x in m.LinkedVehicles.Values
                       where (x.Type == VehicleType.POL_FuStW) && (x.FMS == 4 || x.FMS == 5)
                       select x;

            if (cars.Count() < count) { Print.Error("HandlePOL_Cell", "Zu wenig Polizeifahrzeuge für die Gefangenen an Ort."); return false; }

            for (int j = 0; j < count; j++)
            {

                var selected = cars.ElementAt(j);
                var cells = await a.GetCellOptions(selected.ID);

                if (cells.IsEmpty)
                {

                    Print.AwaitTask("keine Zelle frei");
                    Print.FinishTask(Print.FinishTaskResult.ABBRUCH);

                    Print.StartTask(3, "[Häftlinge] werden entlassen.", Print.LogLevel.LOG_HIGH);
                    if (await a.InvokeFreePrisoner(m.ID))
                    {
                        Print.FinishTask(Print.FinishTaskResult.OK);
                    }
                    else
                    {
                        Print.FinishTask(Print.FinishTaskResult.FEHLER);
                    }

                    return true;

                }
                else
                {

                    Print.StartTask(3, "[Häftling] wird von [" + selected.Title + "] transportiert", Print.LogLevel.LOG_HIGH);
                    Cell c = cells.SuitableCell;
                    Print.AwaitTask(c.Title);
                    
                    if (await a.InvokeTransportPrisoner(selected.ID, c.ID))
                    {
                        Print.AwaitTask(1);
                        Print.FinishTask(Print.FinishTaskResult.OK);
                    }
                    else
                    {
                        Print.FinishTask(Print.FinishTaskResult.FEHLER);
                    }

                    count -= 1;

                }

            }

            return true;

        }
        return false;

    }

    #endregion

    //#########################################################################################

    private API a;

    private static DateTime startTime;
    private static long finishedCounter;

    private static long startCredits;
    private static long lastCredits;

    private static long currentCredits;
    private static long averageCredits;

    private static Dictionary<string, Mission> ActiveMissions;
    private static Dictionary<string, Vehicle> ActiveVehicles;

    //#########################################################################################

    private static Dictionary<string, string> UserSettings = new Dictionary<string, string>();

    //#########################################################################################

    private class API
    {

        private string _user;
        private string _pass;

        private CookieContainer _cookieJar;
        private string _accessToken;

        private readonly string _host = "https://www.leitstellenspiel.de";

        //#########################################################################################

        public API(string username, string password) { _user = username; _pass = password; }

        //#########################################################################################

        public async Task<bool> Login()
        {

            _cookieJar = new CookieContainer();
            _accessToken = string.Empty;

            //AccessToken parsen
            HttpWebRequest request = GetRequest(new Uri(_host), "users/sign_in");
            if (request == null) { return false; }

            ResponseObject response = await GetResponse(request);
            if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK) { _accessToken = ParseLoginAccessToken(response.Data); }
            if (string.IsNullOrEmpty(_accessToken)) { return false; }

            //Login
            string loginData = "utf8=%E2%9C%93&authenticity_token=" + Uri.EscapeDataString(_accessToken) + "&user%5Bemail%5D=" + Uri.EscapeDataString(_user) +
                               "&user%5Bpassword%5D=" + Uri.EscapeDataString(_pass) + "&user%5Bremember_me%5D=0&commit=Einloggen";

            request = await PostRequest(GetRequest(new Uri(_host), "users/sign_in"), ContentType.WWW_FORM, ContentType.HTML, loginData);
            if (request == null) { return false; }

            response = await GetResponse(request);
            if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
            {
                return true;
            }

            return false;

        }

        //#########################################################################################

        public async Task<GetMissionsArgs> GetMissions()
        {
            try
            {
                HttpWebRequest request = GetRequest(new Uri(_host), "");
                if (request == null) { return new GetMissionsArgs(); }

                ResponseObject response = await GetResponse(request);
                if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
                {
                    string doc = response.Data;

                    //Credits aktualisieren
                    var creditIndex = doc.IndexOf("creditsUpdate(") + 14;
                    long currentCredits = long.Parse(doc.Substring(creditIndex, doc.IndexOf(")", creditIndex) - creditIndex));

                    //Einsätze parsen
                    var currentMissions = ParseMissionMarkers(doc);

                    return new GetMissionsArgs(currentMissions, currentCredits);

                }
            }
            catch (Exception) { }

            return new GetMissionsArgs();
        }
        public async Task<GetMissionDetailArgs> GetMissionDetail(string missionId, MissionCategory special)
        {
            try
            {
                string url = "missions/" + missionId;
                HttpWebRequest request = GetRequest(new Uri(_host), url);
                if (request == null) { return null; }

                ResponseObject response = await GetResponse(request);
                if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
                {
                    string doc = response.Data;

                    //MissLoad
                    url = "missions/" + missionId + "/missing_vehicles";
                    request = GetRequest(new Uri(_host), url);
                    if (request == null) { return null; }

                    response = await GetResponse(request);
                    if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
                    {
                        doc = doc + "<div id=\"missLoad\">" + response.Data + "</div>";
                    }

                    //TimeSpan
                    var eventETA = ParseMissionETA(doc, missionId);
                    var vehicleETA = ParseAlertedVehiclesETA(doc);

                    //EventState feststellen
                    var isNew = !doc.Contains("/backalarmAll");

                    var alertedVehicle = ParseVehiclesAlerted(doc, missionId);
                    var arrivedVehicle = ParseVehiclesArrived(doc, missionId, (special == MissionCategory.GEPLANT));
                    var missingVehicle = ParseVehiclesMissing(doc);
                    var availableVehicles = ParseVehiclesAvailable(doc);

                    //Patients parsen
                    var patients = ParsePatients(doc);
                    foreach (var pat in patients)
                    {
                        missingVehicle.AddPatient(pat, (MissionCategory.KRANKENTRANSPORT == special));
                    }

                    //Prisoners parsen
                    var prisonersCount = ParsePrisonersCount(doc);
                    missingVehicle.SetPrisoners(prisonersCount);

                    //EasterEgg
                    string easterEgg = "missions/" + missionId + "/easteregg";
                    if (doc.Contains(easterEgg))
                    {
                        await InvokeEasterEgg(easterEgg);
                    }

                    //Finished?
                    if (doc.Contains("missionNotFound"))
                    {
                        return new GetMissionDetailArgs();
                    }

                    //EventDetail erstellen
                    var details = new GetMissionDetailArgs(isNew, (MissionCategory.GEPLANT == special),
                        alertedVehicle, arrivedVehicle, availableVehicles, missingVehicle, patients, prisonersCount,
                        eventETA, vehicleETA, doc);

                    return details;

                }

            }
            catch (Exception) { }

            return null;
        }

        public async Task<GetHospitalsArgs> GetHospitalOptions(string vehicleID)
        {
            try
            {
                string url = "vehicles/" + vehicleID;
                HttpWebRequest request = GetRequest(new Uri(_host), url);
                if (request == null) { return new GetHospitalsArgs(); }

                ResponseObject response = await GetResponse(request);
                if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
                {
                    string doc = response.Data;
                    var hospitals = ParseHospitals(doc);

                    //Options erstellen
                    var options = new GetHospitalsArgs(hospitals);
                    if (options.IsEmpty)
                    {

                    }
                    return options;

                }

            }
            catch (Exception) { }

            return new GetHospitalsArgs();
        }
        public async Task<GetCellsArgs> GetCellOptions(string vehicleID)
        {
            try
            {
                string url = "vehicles/" + vehicleID;
                HttpWebRequest request = GetRequest(new Uri(_host), url);
                if (request == null) { return new GetCellsArgs(); }

                ResponseObject response = await GetResponse(request);
                if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
                {
                    string doc = response.Data;
                    var cells = ParseCells(doc);
                    var missionId = ParseMissionId(doc);

                    //Options erstellen
                    var options = new GetCellsArgs(missionId, cells);
                    return options;

                }

            }
            catch (Exception) { }

            return new GetCellsArgs();
        }

        //#########################################################################################

        public async Task<GetBuildingsArgs> GetBuildings()
        {
            try
            {
                HttpWebRequest request = GetRequest(new Uri(_host), "");
                if (request == null) { return new GetBuildingsArgs(); }

                ResponseObject response = await GetResponse(request);
                if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
                {
                    string doc = response.Data;

                    var buildings = await ParseBuildings(doc);
                    return new GetBuildingsArgs(buildings);

                }
            }
            catch (Exception) { }

            return new GetBuildingsArgs();
        }

        //#########################################################################################

        #region ReturnArgs

        public class GetMissionsArgs
        {
            public bool IsEmpty { get; }

            //#########################################################################################

            public long Credits { get; }
            public List<Mission> Missions { get; }

            //#########################################################################################

            public GetMissionsArgs(List<Mission> list, long credits) { IsEmpty = false; Missions = list; Credits = credits; }
            public GetMissionsArgs() { IsEmpty = true; }

        }
        public class GetMissionDetailArgs
        {
            public bool IsEmpty { get; }

            //#########################################################################################

            public bool IsNew { get; }
            public bool IsPlanned { get; }

            public TimeSpan ETA { get; }
            public TimeSpan VehicleETA { get; }

            public List<Vehicle> VehiclesAlerted { get; }
            public List<Vehicle> VehiclesArrived { get; }
            public List<Vehicle> VehiclesAvailable { get; }

            public MissionMissing Missing { get; }

            public List<Patient> Patients { get; }
            public int Prisoners { get; }

            public string Debug { get; }

            //#########################################################################################

            public GetMissionDetailArgs() { IsEmpty = true; }
            public GetMissionDetailArgs(bool isnew, bool isplanned, List<Vehicle> vehicles_alerted, List<Vehicle> vehicles_arrived, List<Vehicle> vehicles_available, MissionMissing missing, List<Patient> patients, int prisoners, TimeSpan eventEta, TimeSpan vehiclesEta, string debug)
            {

                //Variablen setzen
                IsEmpty = false;

                IsNew = isnew;
                IsPlanned = isplanned;

                VehiclesAlerted = vehicles_alerted;
                VehiclesArrived = vehicles_arrived;
                VehiclesAvailable = vehicles_available;
                Missing = missing;
                ETA = eventEta;
                VehicleETA = vehiclesEta;
                Patients = patients;
                Prisoners = prisoners;

                Debug = debug;

            }

        }

        public class GetHospitalsArgs
        {
            public bool IsEmpty { get; }

            //#########################################################################################

            public Hospital SuitableHospital
            {
                get
                {
                    if (Hospitals.Count == 0) { return null; }

                    var h = from x in Hospitals where x.FreeSlots > 0 orderby x.IsSuitable descending select x;
                    if (h.Count() == 0) { return null; }

                    return h.First();
                }
            }
            public List<Hospital> Hospitals { get; }

            //#########################################################################################

            public GetHospitalsArgs(List<Hospital> hospitals)
            {
                var free = from x in hospitals where x.FreeSlots > 0 select x;
                IsEmpty = (free.Count() == 0);
                Hospitals = hospitals;
            }
            public GetHospitalsArgs() { IsEmpty = true; }
        }
        public class GetCellsArgs
        {
            public bool IsEmpty { get; }

            //#########################################################################################

            public string MissionID { get; }

            public Cell SuitableCell
            {
                get
                {
                    if (Cells.Count == 0) { return null; }

                    var h = from x in Cells where x.FreeSlots > 0 select x;
                    if (h.Count() == 0) { return null; }

                    return h.First();
                }
            }
            public List<Cell> Cells { get; }

            //#########################################################################################

            public GetCellsArgs(string missionId, List<Cell> cells)
            {
                MissionID = missionId;

                var free = from x in cells where x.FreeSlots > 0 select x;
                IsEmpty = (free.Count() == 0);
                Cells = cells;
            }
            public GetCellsArgs() { IsEmpty = true; }

        }

        public class GetBuildingsArgs
        {
            public bool IsEmpty { get; }

            //#########################################################################################

            public List<Building> Buildings { get; }

            public List<Building> MissingPersonal => (from x in Buildings where x.HasPersonalDemand && !x.IsPersonalInHire select x).ToList();

            //#########################################################################################

            public GetBuildingsArgs(List<Building> list) { IsEmpty = false; Buildings = list; }
            public GetBuildingsArgs() { IsEmpty = true; }

        }

        #endregion

        #region Parser 

        private string ParseLoginAccessToken(string html)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var x = doc.DocumentNode.SelectSingleNode("//form[@id='new_user']");
                var y = x.SelectSingleNode("//input[@name='authenticity_token']");

                return y.GetAttributeValue("value", string.Empty);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        //#########################################################################################

        private List<Mission> ParseMissionMarkers(string html)
        {
            List<Mission> rawList = new List<Mission>();

            int index = -1;
            do
            {
                index = html.IndexOf("missionMarkerAdd("); //17
                if (index >= 0)
                {
                    html = html.Substring(index + 17);
                    int endIndex = html.IndexOf(");");
                    string tmpEvent = html.Substring(0, endIndex);

                    //ID  
                    int index_id = tmpEvent.IndexOf("\"id\":");
                    string missionId = tmpEvent.Substring(index_id + 5, tmpEvent.IndexOf(",", index_id) - index_id - 5);

                    //Title
                    int index_title = tmpEvent.IndexOf("\"caption\":");
                    string eventTitle = tmpEvent.Substring(index_title + 10, tmpEvent.IndexOf(",", index_title) - index_title - 10);

                    //Spezialisierung feststellen
                    MissionCategory special = MissionCategory.NORMAL;

                    //Planned
                    int index_sw = tmpEvent.IndexOf("\"sw\":");
                    string eventSw = tmpEvent.Substring(index_sw + 5, tmpEvent.IndexOf(",", index_sw) - index_sw - 5);
                    if (eventSw == "true") { special = MissionCategory.GEPLANT; }

                    //KT
                    int index_ktw = tmpEvent.IndexOf("\"kt\":");
                    string eventKtw = tmpEvent.Substring(index_ktw + 5, tmpEvent.IndexOf(",", index_ktw) - index_ktw - 5);
                    if (eventKtw == "true") { special = MissionCategory.KRANKENTRANSPORT; }

                    //VerbandID
                    int index_alliance = tmpEvent.IndexOf("\"alliance_id\":");
                    string eventAlliance = tmpEvent.Substring(index_alliance + 14, tmpEvent.IndexOf(",", index_alliance) - index_alliance - 14);
                    if (eventAlliance != "null") { special = MissionCategory.VERBAND; }

                    Mission tmp = new Mission(missionId, eventTitle, special);
                    if (special != MissionCategory.VERBAND) { rawList.Add(tmp); }

                }
            } while (index >= 0);

            return rawList;
        }

        private List<Patient> ParsePatients(string html)
        {

            List<Patient> current = new List<Patient>();

            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);

            var y = doc.DocumentNode.SelectNodes("//div[contains(@class, 'mission_patient')]");
            if (y == null) { return current; }

            foreach (var item in y)
            {
                int index = item.InnerHtml.IndexOf("patientBarColor({") + 17;
                if (index >= 0)
                {
                    string tmpPatHtml = item.InnerHtml.Substring(index);
                    int endIndex = tmpPatHtml.IndexOf("});"); tmpPatHtml = tmpPatHtml.Substring(0, endIndex);

                    //ID  
                    int index_id = tmpPatHtml.IndexOf("\"id\":");
                    string patId = tmpPatHtml.Substring(index_id + 5, tmpPatHtml.IndexOf(",", index_id) - index_id - 5);

                    //Name
                    int index_name = tmpPatHtml.IndexOf("\"name\":");
                    string patName = tmpPatHtml.Substring(index_name + 7, tmpPatHtml.IndexOf(",", index_name) - index_name - 7);

                    //Event
                    int index_event = tmpPatHtml.IndexOf("\"mission_id\":");
                    string patEvent = tmpPatHtml.Substring(index_event + 13, tmpPatHtml.IndexOf(",", index_event) - index_event - 13);

                    //MissingText  
                    int index_missing = tmpPatHtml.IndexOf("\"missing_text\":");
                    string patMissing = tmpPatHtml.Substring(index_missing + 15, tmpPatHtml.IndexOf(",\"", index_missing) - index_missing - 15);

                    //PercSpeed 
                    int index_speed = tmpPatHtml.IndexOf("\"miliseconds_by_percent\":");
                    string patSpeed = tmpPatHtml.Substring(index_speed + 25, tmpPatHtml.IndexOf(",", index_speed) - index_speed - 25);

                    //LivePerc 
                    int index_live = tmpPatHtml.IndexOf("\"live_current_value\":");
                    string patLive = tmpPatHtml.Substring(index_live + 21);

                    Patient patient = new Patient(patId, patEvent, patName, int.Parse(patLive), int.Parse(patSpeed), patMissing);
                    current.Add(patient);
                }
            }

            return current;

        }

        private int ParsePrisonersCount(string html)
        {

            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);

            var y = doc.DocumentNode.SelectSingleNode("//h4[@id='h2_prisoners']");
            if (y == null) { return 0; }

            if (y.InnerText.Contains("Gefangene"))
            {
                if (int.TryParse(y.InnerText.Trim().Split(" ").ElementAt(0), out int count))
                {
                    return count;
                }
            }

            return 0;

        }

        private async Task<List<Building>> ParseBuildings(string html)
        {

            string noSpace = html.Replace(" ", "");

            //UserId
            int i_user = noSpace.IndexOf("varuser_id=") + 11;
            string userId = noSpace.Substring(i_user);
            userId = userId.Substring(0, userId.IndexOf(";"));

            //BuildingMarker parsen
            List<string> rawList = new List<string>();
            int index = -1;
            do
            {
                index = noSpace.IndexOf("buildingMarkerAdd("); //17
                if (index >= 0)
                {
                    noSpace = noSpace.Substring(index + 17);
                    int endIndex = noSpace.IndexOf(");");
                    string tmpEvent = noSpace.Substring(0, endIndex);

                    //ID  
                    int index_id = tmpEvent.IndexOf("\"id\":");
                    string buildingId = tmpEvent.Substring(index_id + 5, tmpEvent.IndexOf(",", index_id) - index_id - 5);

                    //UserId
                    int index_user = tmpEvent.IndexOf("\"user_id\":");
                    string buildingUserId = tmpEvent.Substring(index_user + 10, tmpEvent.IndexOf(",", index_user) - index_user - 10);

                    //Type  
                    int index_type = tmpEvent.IndexOf("\"building_type\":");
                    string buildingType = tmpEvent.Substring(index_type + 16, tmpEvent.IndexOf(",", index_type) - index_type - 16);
                    BuildingType type = (BuildingType)(int.Parse(buildingType));

                    //Zur Abfrage speichern, wenn Leitstelle & dem User gehörend
                    if (type == BuildingType.LEITSTELLE && userId == buildingUserId)
                    {
                        rawList.Add(buildingId);
                    }

                }
            } while (index >= 0);

            //Leitstellen durchlaufen & Gebäude parsen
            List<Building> buildings = new List<Building>();
            foreach (var lst_id in rawList)
            {
                buildings.AddRange(await ParseLeitstelleBuildings(lst_id));
            }

            return buildings;

        }

        //#########################################################################################

        private string ParseMissionId(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var x = doc.DocumentNode.SelectSingleNode("//a[@id='btn_to_mission_place']");
            if (x == null) { return ""; }

            var link = x.GetAttributeValue("href", "/");
            return link.Split("/").Last();
        }

        private TimeSpan ParseMissionETA(string html, string missionId)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var timeIndex = html.IndexOf("missionCountdown(");
                if (timeIndex < 0) { return TimeSpan.Zero; }

                timeIndex += 17;
                var tmp = html.Substring(timeIndex, html.IndexOf(",", timeIndex) - timeIndex);
                int timeSecs = int.Parse(tmp);

                if (timeSecs <= 0) { return TimeSpan.Zero; }

                return TimeSpan.FromSeconds(timeSecs);
            }
            catch (Exception)
            {
                return TimeSpan.Zero;
            }
        }
        private TimeSpan ParseAlertedVehiclesETA(string html)
        {
            try
            {
                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                int maxValue = 0;

                var x = doc.DocumentNode.SelectNodes("//table[@id='mission_vehicle_driving']/tr");
                if (x == null) { return TimeSpan.Zero; }

                foreach (var item in x)
                {

                    string vehicleId = item.GetAttributeValue("id", "").Replace("vehicle_row_", "");
                    if (!string.IsNullOrEmpty(vehicleId))
                    {

                        var y = item.SelectSingleNode("//td[@id='vehicle_drive_" + vehicleId + "']");
                        if (y != null)
                        {
                            int sec = y.GetAttributeValue("sortvalue", 0);
                            if (sec > maxValue) { maxValue = sec; }
                        }

                    }
                }

                return TimeSpan.FromSeconds(maxValue);
            }
            catch (Exception)
            {
                return TimeSpan.Zero;
            }
        }

        private List<Vehicle> ParseVehiclesAlerted(string html, string missionId)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var vehiclesList = new List<Vehicle>();

                var x = doc.DocumentNode.SelectNodes("//table[@id='mission_vehicle_driving']/tr");
                if (x == null) { return new List<Vehicle>(); }

                foreach (var item in x)
                {

                    string vehicleId = item.GetAttributeValue("id", "").Replace("vehicle_row_", "");
                    if (!string.IsNullOrEmpty(vehicleId))
                    {

                        int men = int.Parse(item.Descendants("td").ElementAt(4)?.GetAttributeValue("sortvalue", "-1"));

                        Vehicle v = new Vehicle(vehicleId, men);
                        v.SetFMS(3);

                        vehiclesList.Add(v);

                    }

                }

                return vehiclesList;
            }
            catch (Exception) { }

            return new List<Vehicle>();
        }
        private List<Vehicle> ParseVehiclesArrived(string html, string missionId, bool isPlanned)
        {
            try
            {
                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                var vehiclesList = new List<Vehicle>();

                var x = doc.DocumentNode.SelectNodes("//table[@id='mission_vehicle_at_mission']/tr");
                if (x == null) { return new List<Vehicle>(); }

                foreach (var item in x)
                {

                    string vehicleId = item.GetAttributeValue("id", "").Replace("vehicle_row_", "");
                    if (!string.IsNullOrEmpty(vehicleId))
                    {

                        if (item.Descendants("td").Count() >= 4)
                        {
                            string title = item.Descendants("td").ElementAt(1).Descendants("a").ElementAt(0).InnerText;
                            string typeRaw = item.Descendants("td").ElementAt(1).Descendants("small").ElementAt(0).InnerText.Trim().Trim('(').Trim(')');
                            string station = item.Descendants("td").ElementAt(2).InnerText.Trim();
                            int fms = int.Parse(item.Descendants("td").ElementAt(0).InnerText.Trim());
                            int menCount = int.Parse(item.Descendants("td").ElementAt(3).InnerText.Trim());

                            bool hasPatient = item.Descendants("td").ElementAt(1).InnerText.Contains("Patient:");
                            if (isPlanned) { hasPatient = true; } //Wenn geplant, d.h. Absicherung --> Kein Einsatzabbruch, wenn keine Patientenversorgung

                            Vehicle v = new Vehicle(vehicleId, title, typeRaw, -1, station, fms, false, false, string.Empty, hasPatient, item.OuterHtml);
                            vehiclesList.Add(v);
                        }
                        else
                        {
                            vehiclesList.Add(new Vehicle(vehicleId, -1));
                        }

                    }

                }

                return vehiclesList;
            }
            catch (Exception) { }

            return new List<Vehicle>();
        }
        private List<Vehicle> ParseVehiclesAvailable(string html)
        {
            try
            {
                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                var vehicles = new List<Vehicle>();

                var x = doc.DocumentNode.SelectNodes("//tbody[@id='vehicle_show_table_body_all']/tr");
                var xR = doc.DocumentNode.SelectNodes("//tbody[@id='vehicle_show_table_body_rett']/tr");
                var xLoad = doc.DocumentNode.SelectNodes("//div[@id='missLoad']/tr");

                if (x == null) { x = xR; }
                if (x == null) { return new List<Vehicle>(); }
                if (xLoad != null) 
                { foreach (var item in xLoad) { x.Add(item); } }

                foreach (var item in x)
                {

                    //Parsen
                    string title = item.GetAttributeValue("vehicle_caption", string.Empty);
                    string typeRaw = item.GetAttributeValue("vehicle_type", string.Empty);
                    string station = item.GetAttributeValue("building", string.Empty);

                    var y = item.Descendants("input").First(); // ("input"); // .SelectNodes("//input[@class='vehicle_checkbox']");

                    string vehicleId = y.GetAttributeValue("value", string.Empty);
                    int fms = y.GetAttributeValue("fms", 6);
                    int water = y.GetAttributeValue("wasser_amount", 0);

                    bool isTrailer = (y.GetAttributeValue("trailer", 0) == 1);
                    bool isTractorAvailable = !(y.GetAttributeValue("disabled", "#") == "disabled");
                    string linkedTractorId = y.GetAttributeValue("tractive_vehicle_id", string.Empty);

                    //Erstellen
                    if (!string.IsNullOrWhiteSpace(vehicleId))
                    {
                        Vehicle v = new Vehicle(vehicleId, title, typeRaw, water, station, fms, isTrailer, isTractorAvailable, linkedTractorId, false, item.OuterHtml);
                        vehicles.Add(v);
                    }

                }

                return vehicles;
            }
            catch (Exception)
            {
                return new List<Vehicle>();
            }
        }

        private MissionMissing ParseVehiclesMissing(string html)
        {
            try
            {

                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                MissionMissing current = new MissionMissing();

                //MissingText
                var x = doc.DocumentNode.SelectSingleNode("//div[@id='missing_text']");
                if (x == null) { return current; }
                string missingFW = x.InnerText.Trim();
                bool empty = string.IsNullOrWhiteSpace(missingFW);
                if (!empty) { current.AddMissingText(missingFW); }

                //Gefangene


                return current;
            }
            catch (Exception) { }

            return new MissionMissing();
        }

        //#########################################################################################

        private List<Hospital> ParseHospitals(string html)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var hospitalList = new List<Hospital>();

                var x = doc.DocumentNode.SelectNodes("//tr");
                if (x == null) { return new List<Hospital>(); }

                foreach (var item in x)
                {
                    var y = item.Descendants("td");
                    if (y != null && y.Count() == 5 &&
                       y.ElementAt(4).InnerHtml.Contains("btn_approach_") &&
                       y.ElementAt(0).InnerHtml.Contains("div"))
                    {

                        string title = y.ElementAt(0).InnerHtml.Trim(); title = title.Substring(0, title.IndexOf("<")).Trim();
                        int slots = int.Parse(y.ElementAt(2).InnerText);
                        float km = float.Parse(y.ElementAt(1).InnerText.Replace("km", "").Trim());
                        bool isSuitable = (y.ElementAt(3).InnerText.Trim()) == "Ja";

                        string id = y.ElementAt(4).Descendants("a")?.First().GetAttributeValue("id", string.Empty);
                        id = id.Replace("btn_approach_", "");

                        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(id) && (km < 40))
                        {
                            Hospital h = new Hospital(id, title, slots, isSuitable);
                            hospitalList.Add(h);
                        }

                    }
                }

                return hospitalList;

            }
            catch (Exception) { }

            return new List<Hospital>();
        }
        private List<Cell> ParseCells(string html)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var cellList = new List<Cell>();

                var x = doc.DocumentNode.SelectNodes("//a[contains(@href, 'gefangener')]");
                if (x == null) { return new List<Cell>(); }

                foreach (var item in x)
                {
                    string raw = item.InnerText.Trim();

                    string id = item.GetAttributeValue("href", "").Split("/").Last();
                    string title = raw.Substring(0, raw.IndexOf("(")); //Polizeirevier DD - Mitte(Freie Zellen: 0, Entfernung: 0, 86 km)

                    int i_slots = raw.IndexOf("Freie Zellen:") + 13;
                    int free = int.Parse(raw.Substring(i_slots, raw.IndexOf(",", i_slots) - i_slots));

                    if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(id))
                    {
                        Cell c = new Cell(id, title, free);
                        cellList.Add(c);
                    }
                }

                return cellList;

            }
            catch (Exception) { }

            return new List<Cell>();
        }

        //#########################################################################################

        private async Task<List<Building>> ParseLeitstelleBuildings(string lstId)
        {

            //Leitstellenseite öffnen
            try
            {
                HttpWebRequest request = GetRequest(new Uri(_host), "buildings/" + lstId + "/leitstelle-buildings");
                if (request == null) { return new List<Building>(); }

                ResponseObject response = await GetResponse(request);
                if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
                {

                    string html = response.Data;
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    Dictionary<string, Building> list = new Dictionary<string, Building>();

                    //Tabelle (Gebäudeübersicht suchen)
                    var bList = doc.DocumentNode.SelectNodes("//table[@id='building_table']/tbody/tr");
                    foreach (var item in bList)
                    {

                        var a = item.Descendants("td").ElementAt(1).Descendants("a").ElementAt(0);
                        int typeValue = int.Parse(a.GetAttributeValue("building_type", "-1"));
                        string title = a.InnerText.Trim();
                        string id = a.GetAttributeValue("href", "/buildings/").Replace("/buildings/", "");

                        int persCount = 0;
                        int persTarget = 0;
                        bool inHire = false;

                        var demandNode = item.SelectSingleNode("//div[@id='building_personal_count_target_" + id + "']");
                        if (demandNode != null)
                        {
                            persCount = int.Parse(item.Descendants("td").ElementAt(4).InnerText.Trim());
                            persTarget = int.Parse(demandNode.InnerText.Trim());
                            inHire = (item.Descendants("td").ElementAt(3).Descendants("a").Count() == 0);
                        }

                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title) && typeValue >= 0)
                        {
                            Building b = new Building(id, title, (BuildingType)typeValue, persCount, persTarget, inHire);
                            if (!list.ContainsKey(b.ID)) { list.Add(b.ID, b); }
                        }

                    }

                    return list.Values.ToList();

                }

            }
            catch (Exception) { }

            return new List<Building>();

        }

        #endregion
        #region Invoke

        public async Task<bool> InvokeMissionGeneration()
        {
            HttpWebRequest request = GetRequest(new Uri(_host), "mission-generate?_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (request == null) { return false; }

            ResponseObject response = await GetResponse(request);
            if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
            {
                return true;
            }
            return false;
        }
        public async Task<bool> InvokeEasterEgg(string path)
        {
            HttpWebRequest request = GetRequest(new Uri(_host), path);
            if (request == null) { return false; }

            ResponseObject response = await GetResponse(request);
            if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
            {
                return true;
            }
            return false;
        }

        public async Task<bool> InvokeDoHire(string buildingId, int days)
        {

            if (days > 3) { days = 3; }
            if (days < 0) { return true; }

            HttpWebRequest request = GetRequest(new Uri(_host), "buildings/" + buildingId + "/hire_do/" + days);
            if (request == null) { return false; }

            ResponseObject response = await GetResponse(request);
            if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
            {
                return true;
            }
            return false;
        }

        public async Task<bool> InvokeTestMission(string missionId, string vehicleId)
        {

            string alertData = "utf8=%E2%9C%93&authenticity_token=" + Uri.EscapeDataString(_accessToken) + "&commit=Alarmieren&next_mission=0&alliance_mission_publish=0&vehicle_ids%5B%5D=" + Uri.EscapeDataString(vehicleId);

            HttpWebRequest request = await PostRequest(GetRequest(new Uri(_host), "missions/" + missionId + "/alarm"), ContentType.WWW_FORM, ContentType.HTML, alertData);
            if (request == null) { return false; }

            ResponseObject response = await GetResponse(request);
            if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
            {
                return true;
            }
            return false;
        }
        public async Task<bool> InvokeResetMission(string missionId)
        {
            HttpWebRequest request = GetRequest(new Uri(_host), "missions/" + missionId + "/backalarmAll");
            if (request == null) { return false; }

            ResponseObject response = await GetResponse(request);
            if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
            {
                return true;
            }
            return false;
        }
        public async Task<bool> InvokeResetVehicle(string vehicleId)
        {
            HttpWebRequest request = GetRequest(new Uri(_host), "vehicles/" + vehicleId + "/backalarm");
            if (request == null) { return false; }

            ResponseObject response = await GetResponse(request);
            if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
            {
                return true;
            }
            return false;
        }

        public async Task<string> InvokeAlarm(string missionId, VehicleAlert alarm)
        {

            int alertCount = 0;

            string alertData = "utf8=%E2%9C%93&authenticity_token=" + Uri.EscapeDataString(_accessToken) + "&commit=Alarmieren&next_mission=0&alliance_mission_publish=0";
            foreach (var id in alarm.ToAlert)
            {
                if(ActiveVehicles.ContainsKey(id) && ActiveVehicles[id].IsTrailer)
                {
                    alertData += "&vehicle_ids%5B%5D=" + Uri.EscapeDataString(id);
                    alertCount += 1;
                }
            }
            foreach (var id in alarm.ToModeTractor)
            {
                alertData += "&vehicle_mode%5B" + Uri.EscapeDataString(id) + "%5D=2";
            }
            foreach (var id in alarm.ToAlert)
            {
                alertData += "&vehicle_ids%5B%5D=" + Uri.EscapeDataString(id);
                alertCount += 1;
            }
            
            HttpWebRequest request = await PostRequest(GetRequest(new Uri(_host), "missions/" + missionId + "/alarm"), ContentType.WWW_FORM, ContentType.HTML, alertData);
            if (request == null) { return string.Empty; }

            ResponseObject response = await GetResponse(request);
            if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
            {

                string html = response.Data;
                var doc = new HtmlAgilityPack.HtmlDocument(); doc.LoadHtml(html);
                var error = doc.DocumentNode.SelectSingleNode("//div[@class='container-fluid']//div[@class='alert fade in alert-danger ']");

                if(error == null)
                {
                    return string.Empty;
                }
                else
                {
                    return error.InnerText.Trim().Replace("&times;", "");
                }

            }
            return string.Empty;
        }

        public async Task<bool> InvokeTransportPatient(string vehicleId, string hospitalId)
        {
            HttpWebRequest request = GetRequest(new Uri(_host), "vehicles/" + vehicleId + "/patient/" + hospitalId);
            if (request == null) { return false; }

            ResponseObject response = await GetResponse(request);
            if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
            {
                return true;
            }
            return false;
        }
        public async Task<bool> InvokeFreePatient(string vehicleId)
        {
            return await InvokeTransportPatient(vehicleId, "-1");
        }

        public async Task<bool> InvokeTransportPrisoner(string vehicleId, string cellId)
        {
            HttpWebRequest request = GetRequest(new Uri(_host), "vehicles/" + vehicleId + "/gefangener/" + cellId);
            if (request == null) { return false; }

            ResponseObject response = await GetResponse(request);
            if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
            {
                return true;
            }
            return false;
        }
        public async Task<bool> InvokeFreePrisoner(string missionID)
        {

            string data = "_method=post&authenticity_token=" + Uri.EscapeDataString(_accessToken);

            HttpWebRequest request = await PostRequest(GetRequest(new Uri(_host), "missions/" + missionID + "/gefangene/entlassen"), ContentType.WWW_FORM, ContentType.HTML, data);
            if (request == null) { return false; }

            ResponseObject response = await GetResponse(request);
            if (response.StatusCode == ResponseObject.ResponseObjectStatusCode.OK)
            {
                return true;
            }
            return false;
        }

        #endregion

        //#########################################################################################

        #region WebRequests

        private enum ContentType
        {
            ALL,
            WWW_FORM,
            HTML
        }

        private HttpWebRequest GetRequest(Uri host, string relative)
        {
            return GetRequest(new Uri(host, relative));
        }
        private HttpWebRequest GetRequest(Uri uri)
        {
            try
            {
                HttpWebRequest request = WebRequest.CreateHttp(uri);
                request.Timeout = 12000;
                request.CookieContainer = _cookieJar;
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; WOW64; rv:56.0) Gecko/20100101 Firefox/63.0";
                request.Accept = "text/html, application/xhtml+xml, */*";
                request.Headers.Add("Accept-Language", "de-DE");
                request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
                request.AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip;
                request.AllowAutoRedirect = true;
                request.ServicePoint.Expect100Continue = false;

                return request;
            }
            catch (Exception)
            {
                return null;
            }

        }

        private async Task<HttpWebRequest> PostRequest(HttpWebRequest request, ContentType content, ContentType accept, string post)
        {

            try
            {

                //Post in Byte kodieren
                byte[] data = new System.Text.UTF8Encoding().GetBytes(post);

                //Request anpassen
                request.Method = "POST";

                switch (accept)
                {
                    case ContentType.WWW_FORM:
                        request.Accept = "application/x-www-form-urlencoded;";
                        break;

                    case ContentType.HTML:
                        request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
                        break;

                    default:
                        request.Accept = "*/*;";
                        break;
                }

                switch (content)
                {
                    case ContentType.WWW_FORM:
                        request.ContentType = "application/x-www-form-urlencoded;";
                        break;

                    default:
                        request.ContentType = "*/*;";
                        break;
                }

                request.ContentLength = data.Length;

                //POST-Stream einfügen
                if (string.IsNullOrEmpty(post)) { return request; }

                Stream poststream = await request.GetRequestStreamAsync();
                poststream.Write(data, 0, data.Length);
                poststream.Close();

                return request;

            }
            catch (Exception)
            {
                return null;
            }
        }

        //########################################################

        private class ResponseObject
        {
            public enum ResponseObjectStatusCode
            {
                OK,
                CONNECTION_LOST,
                FORBIDDEN,
                ERROR,
                UNSET = -1
            }

            //########################################################

            public ResponseObjectStatusCode StatusCode { get; private set; } = ResponseObjectStatusCode.UNSET;
            public HttpWebResponse Response { get; private set; } = null;
            public string Data { get; private set; } = string.Empty;

            //########################################################

            public ResponseObject(ResponseObjectStatusCode errorcode)
            {
                StatusCode = errorcode;
                Response = null;
            }
            public ResponseObject(HttpWebResponse response)
            {
                StatusCode = ResponseObjectStatusCode.OK;
                Response = response;

                try
                {
                    StreamReader stream = new StreamReader(response.GetResponseStream());
                    string result = stream.ReadToEnd();
                    stream.Close();

                    Data = result;
                }
                catch
                {
                    Data = string.Empty;
                }

            }

            public void Close()
            {
                StatusCode = ResponseObjectStatusCode.UNSET;
                Response.Close();
            }

        }

        private async Task<ResponseObject> GetResponse(HttpWebRequest request)
        {
            try
            {
                HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync();
                if (response.StatusCode == HttpStatusCode.Found)
                {
                    response.Close();
                    request = GetRequest(response.ResponseUri, response.Headers.Get("Location"));
                    request.Referer = response.ResponseUri.ToString();
                    return await GetResponse(request);
                }
                return new ResponseObject(response);
            }
            catch(Exception e)
            {
                return new ResponseObject(ResponseObject.ResponseObjectStatusCode.ERROR);
            }

        }

        #endregion

    }

    //#########################################################################################

    #region UserInterface

    private Dictionary<string, MissionStats> stats_mission = new Dictionary<string, MissionStats>();
        
    private class MissionStats : MissionMissing
    {

        public void Update(MissionMissing m)
        {
            if (m.IsEmpty) { return; }
            if (m.MissingClasses == null) { return; }

            foreach (var v in m.MissingClasses)
            {
                if ((int)v.Key < 1000)
                {
                    if (MissingClasses.ContainsKey(v.Key))
                    {
                        if (v.Value > MissingClasses[v.Key]) { MissingClasses[v.Key] = v.Value; }
                    }
                    else
                    {
                        MissingClasses.Add(v.Key, v.Value);
                    }
                }
            }
        }

    }

    //#########################################################################################

    private void UpdateMissionStats(Mission m)
    {
        if (!stats_mission.ContainsKey(m.ID)) { stats_mission.Add(m.ID, new MissionStats()); }
        if (m.Missing == null) { return; }

        stats_mission[m.ID].Update(m.Missing);
    }

    //#########################################################################################

    private class Print
    {

        public enum LogLevel : short
        {
            LOG_HIGH = 0,
            LOG_LOW = 1
        }

        //#########################################################################################

        private const ConsoleColor DEFAULT_BG = ConsoleColor.Black;
        private const ConsoleColor DEFAULT_FG = ConsoleColor.White;

        private const int consoleWidth = 100;
        private const int levelWidth = 2;

        private const int logIndent = 20;

        //#########################################################################################

        public static void Welcome()
        {
            Console.ForegroundColor = DEFAULT_FG;
            Console.BackgroundColor = DEFAULT_BG;

            Break('#', LogLevel.LOG_LOW);
            Info(0, "LEITSTELLENSPIEL", LogLevel.LOG_LOW);
            Info(0, "ALARMIERUNG-BOT", LogLevel.LOG_LOW);
            Info(0, DateTime.Now.ToString(), LogLevel.LOG_LOW);
            Break('#', LogLevel.LOG_LOW);

        }

        //#########################################################################################

        public enum FinishTaskResult
        {
            OK,
            FEHLER,
            ABBRUCH
        }

        public static void StartTask(int intent, string message, LogLevel level)
        {
            Console.ForegroundColor = GetIntentColor(intent);
            Console.BackgroundColor = DEFAULT_BG;

            //Intent erstellen
            string msg = GetLogLevelPrefix(level);
            for (int i = 0; i < intent; i++)
            {
                msg += "  ";
            }

            //Message ausgeben
            msg += "> " + message + " ... ";
            OutWrite(msg);
        }
        public static void AwaitTask(int span)
        {
            TimeSpan waitSpan = TimeSpan.FromSeconds(span );
            OutWrite(" <Warte " + waitSpan.TotalSeconds.ToString("#0") + "s> ");
            System.Threading.Thread.Sleep(waitSpan);
        }
        public static void AwaitTask(string info)
        {
            OutWrite(" <" + info.Trim() + "> ");
        }
        public static void FinishTask(FinishTaskResult result)
        {
            Console.BackgroundColor = DEFAULT_BG;

            //Tag erstellen
            string tag = "";
            switch (result)
            {
                case FinishTaskResult.OK:
                    Console.ForegroundColor = ConsoleColor.Green;
                    tag = "[OK]";
                    break;
                case FinishTaskResult.FEHLER:
                    Console.ForegroundColor = ConsoleColor.Red;
                    tag = "[FEHLER]";
                    break;
                case FinishTaskResult.ABBRUCH:
                    Console.ForegroundColor = ConsoleColor.Gray;
                    tag = "[ABBRUCH]";
                    break;
            }

            //Zeile abschließen
            OutWriteLine(tag);

            Console.ForegroundColor = DEFAULT_FG;
        }

        //#########################################################################################

        public static void Break(char c, LogLevel level)
        {
            Console.ForegroundColor = DEFAULT_FG;
            OutWriteLine(GetLogLevelPrefix(level) + GetCharLoop(c, consoleWidth));
        }

        public static void Info(int intent, string message, LogLevel level, ConsoleColor overwriteColor = DEFAULT_BG)
        {
            Console.ForegroundColor = GetIntentColor(intent);
            if (overwriteColor != DEFAULT_BG) { Console.ForegroundColor = overwriteColor; }

            //Intent erstellen
            string msg = GetLogLevelPrefix(level) + 
                         GetCharLoop(' ', intent * 2) + 
                         "> " + message;
            OutWriteLine(msg);

            //Farbe zurücksetzen
            Console.ForegroundColor = DEFAULT_FG;
        }
        public static void Error(string group, string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;

            //Intent erstellen
            string msg = DateTime.Now.ToString() + "  ";

            //Message ausgeben
            msg += "> FEHLER [" + group + "] >> " + message;

            TextWriter errorWriter = Console.Error;
            errorWriter.WriteLine();
            errorWriter.WriteLine(msg);
            errorWriter.WriteLine();

            if(Console.IsErrorRedirected)
            {
                OutWriteLine(msg);
            }

            Console.ForegroundColor = DEFAULT_FG;
        }

        //#########################################################################################

        public static void NewLoop(int count, TimeSpan elapsed)
        {
            Console.ForegroundColor = DEFAULT_FG;

            Break('#', LogLevel.LOG_LOW);
            Info(0, "Schleife " + count.ToString() + " | " + DateTime.Now.ToString() + " | " + elapsed.ToString(), LogLevel.LOG_LOW);
            Break('#', LogLevel.LOG_LOW);
        }
        public static void EndLoop(TimeSpan waitSpan)
        {
            Console.ForegroundColor = DEFAULT_FG;

            Break('#', LogLevel.LOG_LOW);
            OutWrite(GetLogLevelPrefix(LogLevel.LOG_LOW) + "Nächste Ausführung: " + (DateTime.Now + waitSpan).ToLongTimeString() + "   //   Wartezeit: " + waitSpan.ToString());
            System.Threading.Thread.Sleep(waitSpan);
            OutWrite("\r                                                                                                      ");
            OutWriteLine("");

        }

        //#########################################################################################

        public static void Overview(List<Mission> list, Dictionary<string, MissionStats> statsList, TimeSpan time, long finished)
        {

            List<string> buffer = new List<string>();

            //Einsatzübersicht
            Break('#', LogLevel.LOG_LOW);
            buffer.Add(GetCharLoop('#', consoleWidth));
            buffer.Add("# LEITSTELLENSPIEL-BOT | Aktualisiert: " + DateTime.Now.ToString());
            buffer.Add("# Kontostand:    " + currentCredits.ToString("N0") + " | ca. " + averageCredits.ToString("N0") + "/Tag");
            buffer.Add("# Einsatzdichte: " + (finished / time.TotalHours).ToString("#0") + " Einsätze/h [Max: 120/h] - Effizienz: " + (((finished/time.TotalHours)/120)*100).ToString("#0.0") + "%");
            buffer.Add(GetCharLoop('#', consoleWidth));

            Dictionary<MissionState, int> stat = new Dictionary<MissionState, int>();
            list.Sort((x, y) => { if (x.State == y.State) { return 0; } else if (x.State < y.State) { return -1; } else { return 1; } });
            foreach (var item in list)
            {
                if (stat.ContainsKey(item.State)) { stat[item.State] += 1; }
                else { stat.Add(item.State, 1); }
            }
            foreach (var item in stat)
            {

                string stateText = item.Key.ToString() + " - " + item.Value + "x";
                Info(2, stateText, LogLevel.LOG_HIGH);
                buffer.Add(GetCharLoop(' ', 4) + "> " + stateText);

                //Status: Im Einsatz detailiert ausgeben
                if (item.Key == Bot.MissionState.IM_EINSATZ)
                {
                    var filter = from x in list where x.State == item.Key select x;

                    int maxLength = 0;
                    foreach (var m in filter) { if (m.Title.Length > maxLength) { maxLength = m.Title.Length; } }

                    foreach (var m in filter)
                    {
                        string line = m.Title + GetIntentSpacing(m.Title, maxLength) + " / ETA: " + m.ETA.ToString();
                        buffer.Add(GetCharLoop(' ', 6) + "> " + line);
                    }
                }
            }

            buffer.Add(GetCharLoop('-', consoleWidth));

            //Statistik: Gesamt
            buffer.Add(GetCharLoop(' ', 2) + "> GESAMT-EINSÄTZE: " + ActiveMissions.Count.ToString() + " Einsätze derzeit in Bearbeitung.");
            buffer.Add(GetCharLoop(' ', 21) + statsList.Count.ToString() + " Einsätze insgesamt.");

            buffer.Add(GetCharLoop('#', consoleWidth));

            //Statistik: Fahrzeuge
            Dictionary<VehicleClass, int> demandAll = new Dictionary<VehicleClass, int>();
            Dictionary<VehicleClass, int> demandLoop = new Dictionary<VehicleClass, int>();
            foreach (var m in statsList)
            {
                foreach (var c in m.Value.MissingClasses)
                {
                    if (demandAll.ContainsKey(c.Key)) { demandAll[c.Key] += c.Value; }
                    else { demandAll.Add(c.Key, c.Value); }

                    if (ActiveMissions.ContainsKey(m.Key))
                    {
                        if (demandLoop.ContainsKey(c.Key)) { demandLoop[c.Key] += c.Value; }
                        else { demandLoop.Add(c.Key, c.Value); }
                    }
                }
            }

            //-- In diesem Einsatz fehlend
            buffer.Add(GetCharLoop(' ', 2) + "> Fahrzeugverfügbarkeit in diesem Durchgang: (Negative Zahlen = Fehlende Fahrzeuge)");
            foreach (var item in demandLoop)
            {

                int avCount = (from x in ActiveVehicles.Values where Vehicle.GetVehicleTypesForClass(item.Key).Contains(x.Type) select x).Count();
                string classText = item.Key.ToString() + GetIntentSpacing(item.Key.ToString());
                int delta = avCount - item.Value;

                buffer.Add(GetCharLoop(' ', 4) + "> " + classText + " DELTA: " + delta.ToString());

            }
            buffer.Add(GetCharLoop('-', consoleWidth));

            //-- Durchschnittlicher Bedarf
            buffer.Add(GetCharLoop(' ', 2) + "> Durchschnittlicher Bedarf pro Einsatz: (" + ActiveMissions.Count + " parallel) - " + statsList.Count + " Datensätze insgesamt");
            foreach (var item in demandAll)
            {

                int avCount = (from x in ActiveVehicles.Values where Vehicle.GetVehicleTypesForClass(item.Key).Contains(x.Type) select x).Count();
                double perM = (double)item.Value / (double)ActiveMissions.Count;
                string classText = item.Key.ToString() + GetIntentSpacing(item.Key.ToString());
                double longDemand = perM * ActiveMissions.Count;

                string mark = "[  ]"; if (avCount < longDemand) { mark = "[--]"; } else if (avCount > longDemand) { mark = "[++]"; }

                buffer.Add(GetCharLoop(' ', 4) + "> " + classText + " - BEDARF: " + perM.ToString("#0.00000") + "/Einsatz | BEDARF: " + longDemand.ToString("#000.0") + "x | ANGEBOT: " + avCount.ToString("#000") + "x | " + mark);

            }
            buffer.Add(GetCharLoop('#', consoleWidth));


            if (!Console.IsOutputRedirected)
            {
                foreach (var item in buffer)
                {
                    Console.WriteLine(item);
                }
            }
            OutFile(UserSettings[SETTING_STATFILE], buffer);

        }

        public static void MissionState(Mission e)
        {
            Console.ForegroundColor = DEFAULT_FG;

            //Texte generieren
            string state = "";
            string add = "";
            switch (e.State)
            {
                case Bot.MissionState.NEU:
                    state = "Status: NEU";
                    add = "";
                    break;
                case Bot.MissionState.GEPLANT:
                    state = "Status: GEPLANT";
                    add = "BEGINN: " + e.ETA.ToString();
                    break;
                case Bot.MissionState.GEPLANT_VORORT:
                    state = "Status: GEPLANT & FAHRZEUGE VOR ORT";
                    add = "BEGINN: " + e.ETA.ToString();
                    break;
                case Bot.MissionState.RESET:
                    state = "Status: RESET";
                    add = "";
                    break;
                case Bot.MissionState.WARTE_AUF_DISPONIERUNG:
                    state = "Status: WARTE AUF DISPONIERUNG";
                    add = "";
                    break;
                case Bot.MissionState.WARTE_AUF_EINTREFFEN:
                    state = "Status: WARTE AUF EINTREFFEN";
                    add = "ETA: " + e.ETA.ToString();
                    break;
                case Bot.MissionState.IM_EINSATZ:
                    state = "Status: IM EINSATZ";
                    add = "ETA: " + e.ETA.ToString();
                    break;
                case Bot.MissionState.BEENDET:
                    state = "Status: ABGESCHLOSSEN";
                    add = "";
                    break;
                case Bot.MissionState.BEENDET_SPRECHWUNSCH:
                    state = "Status: ABGESCHLOSSEN, ABER OFFENE SPRECHWÜNSCHE";
                    var fmsCount = (from x in e.LinkedVehicles.Values where x.FMS == 5 select x).Count();
                    add = "Im Status 5: " + fmsCount;
                    break;
                default:
                    break;
            }

            //Ausgeben
            Info(2, state, LogLevel.LOG_LOW);
            if (!string.IsNullOrEmpty(add)) { Info(3, add, LogLevel.LOG_LOW); }

        }
        public static void MissionMissing(Mission m, ref List<VehicleClass> canceled, ref List<VehicleClass> cancel, ref bool cancelAlert)
        {

            if (!m.Missing.IsEmpty) { Print.Info(2, "Zu Disponieren:", LogLevel.LOG_HIGH); }

            //Klassen
            if (m.Missing.MissingClasses != null && m.Missing.MissingClasses.Count > 0)
            {
                Print.Info(3, "Benötigte Klassen:", LogLevel.LOG_HIGH);
                foreach (var item in m.Missing.MissingClasses)
                {
                    Print.Info(4, item.Key.ToString() + " - " + item.Value + "x", LogLevel.LOG_HIGH);
                    if (canceled.Contains(item.Key)) { Print.StartTask(3, "[" + item.Key + "] bereits in anderem Einsatz benötigt.", LogLevel.LOG_HIGH); Print.FinishTask(Print.FinishTaskResult.ABBRUCH); cancel.Add(item.Key); cancelAlert = true; break; }
                }
            }

        }

        //#########################################################################################

        private static string GetCharLoop(char c, int length)
        {
            if (length <= 0) { return ""; }

            var text = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                text.Append(c);
            }
            return text.ToString();
        }

        public static string GetIntentSpacing(string txt, int intent = logIndent)
        {
            int txtLength = intent - txt.Length;
            return GetCharLoop(' ', txtLength);
        }

        private static ConsoleColor GetIntentColor(int intent)
        {
            switch (intent)
            {
                case 0:
                case 1:
                    return DEFAULT_FG;
                case 2:
                case 3:
                    return ConsoleColor.Gray;
                default:
                    return ConsoleColor.DarkGray;
            }
        }

        private static string GetLogLevelPrefix(LogLevel level)
        {
            return ((short)level).ToString() + "|";           
        }

        //#########################################################################################

        private const int outFixedLines = 3;

        private static void OutWrite(string txt)
        {
            if (true)/*(Console.IsOutputRedirected)*/ { Console.Write(txt); }
            else { OutConsole(txt, true); }
        }
        private static void OutWriteLine(string txt)
        {
            if (true)/*(Console.IsOutputRedirected)*/ { Console.WriteLine(txt); }
            else { OutConsole(txt, false); }
        }

        private static void OutConsole(string msg, bool finishLine)
        {

            //Zeile in Console schreiben
            int oldLeft = Console.CursorLeft;
            int oldTop = Console.CursorTop;
            
            Console.SetCursorPosition(0, oldTop);
            if (oldLeft > 0) { Console.SetCursorPosition(0, oldTop + 1); }
            for (int i = 1; i <= outFixedLines; i++)
            {
                Console.WriteLine(GetIntentSpacing("", consoleWidth + 2)); //LogLevel-String
            }
            Console.SetCursorPosition(oldLeft, oldTop);

            if (finishLine)
            {
                Console.Write(msg);
                oldLeft = Console.CursorLeft;
                oldTop = Console.CursorTop;
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine(msg);
                oldLeft = Console.CursorLeft;
                oldTop = Console.CursorTop;
            }
            
            //Fixed anzeigen
            Console.ForegroundColor = DEFAULT_FG;

            //Break
            StringBuilder line = new StringBuilder(GetLogLevelPrefix(LogLevel.LOG_HIGH));
            for (int i = 0; i < consoleWidth; i++)
            {
                line.Append('#');
            }
            Console.WriteLine(line);

            if (currentCredits > 0) { 

                Console.Write(GetLogLevelPrefix(LogLevel.LOG_HIGH) + "  > CREDIT: ");

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(currentCredits.ToString("N0"));

                Console.ForegroundColor = DEFAULT_FG;
                Console.Write(GetLogLevelPrefix(LogLevel.LOG_HIGH) + "  >   MORE: ");

                double more = currentCredits - startCredits;
                if (more > 0) { Console.ForegroundColor = ConsoleColor.Green; } else { Console.ForegroundColor = ConsoleColor.Red; }
                Console.Write(more.ToString("N0"));

                Console.ForegroundColor = DEFAULT_FG;
                Console.WriteLine(" // seit: " + (DateTime.Now - startTime).TotalHours.ToString("#0.0") + "h // ca. " + averageCredits.ToString("N0") + " pro Tag");

            } else
            {
                Console.WriteLine("  > CREDIT: -- Warte auf Daten --");
                Console.WriteLine();
            }

            //Position zurücksetzen
            Console.SetCursorPosition(oldLeft, oldTop);

        }

        private static void OutFile(string path, List<string> lines)
        {
            try
            {
                using (StreamWriter outputFile = new StreamWriter(path, false))
                {
                    foreach (string line in lines)
                        outputFile.WriteLine(line);
                }
            }
            catch (Exception e)
            {
                Print.Error("Print/OutFile", e.Message);
            }
        }

    }

    #endregion

    #region UserSettings

    const string SETTING_WORKDIR = "working_dir";
    const string SETTING_STATFILE = "statfile_path";
    const string SETTING_LOGIN_USER = "login_username";
    const string SETTING_LOGIN_PASS = "login_pass";

    private void GetUserSettings()
    {

        UserSettings = new Dictionary<string, string>();

        //Arbeitsverzeichnis
        UserSettings.Add(SETTING_WORKDIR, System.IO.Directory.GetParent(System.Reflection.Assembly.GetEntryAssembly().Location).FullName);

        //Einstellungsdatei laden
        string configPath = System.IO.Path.Combine(UserSettings[SETTING_WORKDIR], "config.sven");
        if (!System.IO.File.Exists(configPath))
        {
            Print.Error("GetUserSettings", "Keine Konfigurationsdatei gefunden: " + configPath);
            Environment.Exit(-1);
        }
        string settContent = System.IO.File.ReadAllText(configPath, Encoding.UTF8);
        var settDoc = new HtmlDocument(); settDoc.LoadHtml(settContent);

        //Sett: Logindaten
        var nodeLogin = settDoc.DocumentNode.SelectSingleNode("//login");
        if (nodeLogin == null)
        {
            Print.Error("GetUserSettings/config.sven", "Keine Logindaten gefunden. " + settContent);
            Environment.Exit(-1);
        }
        string user = nodeLogin.GetAttributeValue("user", string.Empty);
        string pass = nodeLogin.GetAttributeValue("pass", string.Empty);
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            Print.Error("GetUserSettings/config.sven", "Logindaten fehlerhaft. " + nodeLogin.OuterHtml);
            Environment.Exit(-1);
        }
        UserSettings.Add(SETTING_LOGIN_USER, user);
        UserSettings.Add(SETTING_LOGIN_PASS, pass);

        //Sett: StatsFilePath
        var nodeStats = settDoc.DocumentNode.SelectSingleNode("//stats");
        if (nodeStats == null)
        {
            Print.Error("GetUserSettings/config.sven", "Kein StatisikElement gefunden. " + settContent);
            Environment.Exit(-1);
        }
        string path = nodeStats.GetAttributeValue("file", string.Empty);
        if (string.IsNullOrEmpty(path))
        {
            Print.Error("GetUserSettings/config.sven", "StatisikElement fehlerhaft. " + nodeStats);
            Environment.Exit(-1);
        }
        if (System.IO.Path.IsPathRooted(path))
        {
            UserSettings.Add(SETTING_STATFILE, path);
        }
        else
        {
            UserSettings.Add(SETTING_STATFILE, System.IO.Path.Combine(UserSettings[SETTING_WORKDIR], path));
        }

    }

    #endregion

    //#########################################################################################

    #region Einsatzobjektmodelle

    private class Mission
    {

        public string ID { get; private set; }
        public string Title { get; private set; }

        //#########################################################################################

        public MissionState State { get; private set; }
        public TimeSpan ETA { get; private set; }
        public MissionCategory Category { get; private set; }

        public MissionMissing Missing { get; private set; }
        public List<Patient> Patients { get; private set; }

        public Dictionary<string, Vehicle> LinkedVehicles { get; private set; }     //Alle Fahrzeuge im Zusammenhang mit diesem Einsatz
        public List<Vehicle> AvailableVehicles => (from x in LinkedVehicles.Values where x.FMS == 2 || x.FMS == 1 select x).ToList();

        public int VehicleAlertedCount => (from x in LinkedVehicles.Values where x.FMS == 3 select x).Count();
        public int VehicleArrivedCount => (from x in LinkedVehicles.Values where x.FMS == 4 select x).Count();

        //#########################################################################################

        public Mission(string id, string title, MissionCategory special) { ID = id; Title = title; Category = special; }

        //#########################################################################################

        public void SetDetail(API.GetMissionDetailArgs d)
        {
            if (d == null) { return; }
            if (d.IsEmpty) { State = MissionState.BEENDET; return; }

            //Daten übernehmen
            Patients = d.Patients;
            Missing = d.Missing;
            ETA = d.ETA;

            MergeVehicles(d.VehiclesAlerted);
            MergeVehicles(d.VehiclesArrived);
            MergeVehicles(d.VehiclesAvailable);

            //Status festlegen

            #region Status: NEU

            if (d.IsNew && !d.IsPlanned)
            {
                State = MissionState.NEU;
                ETA = TimeSpan.Zero;

                //Krankentransport ohne Testalarm disponieren
                if (Category == MissionCategory.KRANKENTRANSPORT && !Missing.IsEmpty) { State = MissionState.WARTE_AUF_DISPONIERUNG; }
            }

            #endregion

            #region Status: GEPLANT

            else if (d.IsPlanned && !Missing.IsEmpty && VehicleAlertedCount == 0 && VehicleArrivedCount == 0)
            {
                State = MissionState.GEPLANT;
                if (ETA.TotalMinutes < 30)
                {
                    //Geplanten Einsatz erst ab Vorlauf 30min starten.
                    //TODO: State = MissionState.WARTE_AUF_DISPONIERUNG;
                }
            }
            else if (d.IsPlanned && Missing.IsEmpty) { State = MissionState.GEPLANT_VORORT; }

            #endregion

            #region Status: WARTE_AUF_DISPONIERUNG

            else if (!d.Missing.IsEmpty && VehicleAlertedCount == 0) { State = MissionState.WARTE_AUF_DISPONIERUNG; }
            else if (!d.IsNew && !d.IsPlanned && VehicleAlertedCount == 0 && VehicleArrivedCount == 0) { Missing.AddSingleLF(); State = MissionState.WARTE_AUF_DISPONIERUNG; }

            #endregion

            #region Status: WARTE_AUF_EINTREFFEN

            else if (!d.Missing.IsEmpty && VehicleAlertedCount > 0) { State = MissionState.WARTE_AUF_EINTREFFEN; ETA = d.VehicleETA; }

            #endregion

            #region Status: BEENDET

            else
            {
                State = MissionState.IM_EINSATZ;
                ETA = CreateRightETA();

                //Es wird noch auf etwas gewartet
                if(ETA == TimeSpan.Zero) { State = MissionState.BEENDET_SPRECHWUNSCH; }
            }

            #endregion

            //Statusanpassungen vornehmen 

            #region Polizei

            if (d.Missing.NeedCell)
            {
                if (d.VehiclesArrived.Count == 0 && d.VehiclesAlerted.Count == 0)
                {
                    Missing.AddSingleFuStW();
                }
            }

            #endregion
            #region Rettungsdienst 

            //LNA ab 5 Patienten
            if (Patients.Count >= 5 || Missing.MissingClasses.ContainsKey(VehicleClass.RD_LNA))
            {
                var lnaCount = (from x in LinkedVehicles.Values where x.Type == VehicleType.RD_LNA && (x.FMS == 3 || x.FMS == 4) select x).Count();
                if (lnaCount >= 1)
                {
                    Missing.ClearDemand(VehicleClass.RD_LNA);
                }
                if (lnaCount == 0)
                {
                    Missing.IncreaseDemand(VehicleClass.RD_LNA, 1, MissionMissing.IncreaseMode.Replace);
                }
            }

            //OrgL ab 10 Patienten
            if (Patients.Count >= 10 || Missing.MissingClasses.ContainsKey(VehicleClass.RD_OrgL))
            {
                var orglCount = (from x in LinkedVehicles.Values where x.Type == VehicleType.RD_OrgL && (x.FMS == 3 || x.FMS == 4) select x).Count();
                if (orglCount >= 1)
                {
                    Missing.ClearDemand(VehicleClass.RD_OrgL);
                }
                if (orglCount == 0)
                {
                    Missing.IncreaseDemand(VehicleClass.RD_OrgL, 1, MissionMissing.IncreaseMode.Replace);
                }
            }

            #endregion

        }
        public void Merge(Mission e)
        {
            if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(e.Title)) { Title = e.Title; }
            if (State != e.State && e.State != MissionState.NEU) { State = e.State; }
            if (ETA != e.ETA) { ETA = e.ETA; }
            if (Missing != e.Missing) { Missing = e.Missing; }
        }

        //#########################################################################################

        public void SetState(MissionState state) { State = state; }

        //#########################################################################################

        private void MergeVehicles(List<Vehicle> list)
        {
            if (LinkedVehicles == null) { LinkedVehicles = new Dictionary<string, Vehicle>(); }

            foreach (var v in list)
            {

                //MissionID vergeben
                if (v.FMS == 2 || v.FMS == 1) { v.AssignMission(); }
                else { v.AssignMission(ID); }

                //Vehicle speichern
                if (LinkedVehicles.ContainsKey(v.ID))
                {
                    LinkedVehicles[v.ID].Merge(v);
                }
                else
                {
                    LinkedVehicles.Add(v.ID, v);
                }

            }
        }

        private TimeSpan CreateRightETA()
        {
            TimeSpan eta = new TimeSpan(ETA.Ticks);
            TimeSpan patEta = TimeSpan.Zero;

            foreach (var item in Patients)
            {
                if (item.PercentageSpeed > 0)
                {
                    long ms = item.LivePercentage * item.PercentageSpeed;
                    if (ms > 0)
                    {
                        TimeSpan t = TimeSpan.FromMilliseconds(ms);
                        if (t > patEta) { patEta = t; }
                    }
                }
            }

            if (eta < patEta) { eta = patEta; }

            return TimeSpan.FromSeconds(eta.TotalSeconds);
        }

    }
    private enum MissionCategory
    {
        NORMAL,

        GEPLANT,
        KRANKENTRANSPORT,
        VERBAND
    }
    private enum MissionState
    {
        NEU,
        GEPLANT,
        GEPLANT_VORORT,
        RESET,
        WARTE_AUF_DISPONIERUNG,
        WARTE_AUF_EINTREFFEN,
        BEENDET,
        BEENDET_SPRECHWUNSCH,
        IM_EINSATZ
    }

    //#########################################################################################

    private class MissionMissing
    {

        public bool IsEmpty { get; protected set; }

        public SortedDictionary<VehicleClass, int> MissingClasses { get; protected set; } = new SortedDictionary<VehicleClass, int>();
        public List<VehicleClass> FlexibleDemand { get; protected set; } = new List<VehicleClass>();
                
        //Polizei
        public bool NeedCell => (MissingClasses.ContainsKey(VehicleClass.PROC_POL_PRISONERS) && MissingClasses[VehicleClass.PROC_POL_PRISONERS] > 0);

        //#########################################################################################

        public MissionMissing() { IsEmpty = true; }

        //#########################################################################################

        public void AddMissingText(string missingText)
        {

            //MissingText formatieren
            var x = new System.Text.StringBuilder(missingText);
            x.Replace("Zusätzlich benötigte Fahrzeuge: ", "");
            x.Replace("Wir benötigen noch min. ", "");

            bool inBrackets = false;
            for (int i = 0; i < x.Length; i++) { if (x[i] == '(') { inBrackets = true; } else if (x[i] == ')') { inBrackets = false; } else if (x[i] == ',' && inBrackets) { x[i] = ' '; } }
            missingText = x.ToString();

            //MissingText durchlaufen
            foreach (var raw in missingText.Split(','))
            {
                string demand = raw.Trim().Trim('.').Trim(',').ToLower();

                int demandCount = 0;
                if (int.TryParse(demand.Split(" ")?[0].Trim(), out int result)) { demandCount = result; }

                #region Feuerwehr

                if (demand.Contains("elw 2")) { IncreaseDemand(VehicleClass.FW_ELW2, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("person mit elw-2 ausbildung")) { IncreaseDemand(VehicleClass.FW_ELW2, (demandCount / 6), IncreaseMode.Replace); }
                else if (demand.Contains("elw 1")) { IncreaseDemand(VehicleClass.FW_ELW1, demandCount, IncreaseMode.Replace); }

                else if (demand.Contains("(lf)")) { IncreaseDemand(VehicleClass.FW_LF, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("rüstwagen")) { IncreaseDemand(VehicleClass.FW_Rüst, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("(dlk 23)")) { IncreaseDemand(VehicleClass.FW_DLK, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("gw-a")) { IncreaseDemand(VehicleClass.FW_GW_Atemschutz, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("gw-öl")) { IncreaseDemand(VehicleClass.FW_GW_Öl, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("schlauchwagen")) { IncreaseDemand(VehicleClass.FW_Schlauchwagen, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("gw-messtechnik")) { IncreaseDemand(VehicleClass.FW_GW_Messtechnik, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("gw-höhenrettung")) { IncreaseDemand(VehicleClass.FW_GW_Höhenrettung, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("gw-gefahrgut")) { IncreaseDemand(VehicleClass.FW_GW_Gefahrgut, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("dekon-p")) { IncreaseDemand(VehicleClass.FW_GW_DekonP, demandCount, IncreaseMode.Replace); }

                else if (demand.Contains("fwk")) { IncreaseDemand(VehicleClass.FW_Kran, demandCount, IncreaseMode.Replace); }

                else if (demand.Contains("flugfeldlöschfahrzeug")) { IncreaseDemand(VehicleClass.FW_FlugfeldLF, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("rettungstreppe")) { IncreaseDemand(VehicleClass.FW_FlugfeldTreppe, demandCount, IncreaseMode.Replace); }

                else if (demand.Contains("turbolöscher")) { IncreaseDemand(VehicleClass.FW_Turbolöscher, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("(tm 50)")) { IncreaseDemand(VehicleClass.FW_TM, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("ulf mit löscharm")) { IncreaseDemand(VehicleClass.FW_ULF, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("gw-werkfeuerwehr")) { IncreaseDemand(VehicleClass.FW_GW_Werkfeuerwehr, demandCount, IncreaseMode.Replace); }

                else if (demand.Contains("l. wasser")) { IncreaseDemand(VehicleClass.PROC_FW_WATER, demandCount, IncreaseMode.Replace); }

                else if (demand.Contains("feuerwehrleute")) { IncreaseDemand(VehicleClass.PROC_FW_MEN, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("1 feuerwehrmann")) { IncreaseDemand(VehicleClass.PROC_FW_MEN, 1, IncreaseMode.Replace); }

                #endregion
                #region Rettungsdienst

                else if (demand.Contains("rtw oder ktw")) { IncreaseDemand(VehicleClass.RD_KTW, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("rtw")) { IncreaseDemand(VehicleClass.RD_RTW, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("gw-san")) { IncreaseDemand(VehicleClass.RD_GW_SAN, demandCount, IncreaseMode.Replace); }

                #endregion
                #region Polizei

                else if (demand.Contains("fustw")) { IncreaseDemand(VehicleClass.POL_FuStW, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("gefangene")) { /* Ignorieren: wird separat behandelt */ }
                else if (demand.Contains("lebefkw")) { IncreaseDemand(VehicleClass.POL_leBefKW, demandCount, IncreaseMode.Replace); }

                else if (demand.Contains("polizeihubschrauber")) { IncreaseDemand(VehicleClass.POL_Hubschrauber, demandCount, IncreaseMode.Replace); }

                else if (demand.Contains("grukw")) { IncreaseDemand(VehicleClass.POL_GruKW, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("fükw")) { IncreaseDemand(VehicleClass.POL_FüKW, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("gefkw")) { IncreaseDemand(VehicleClass.POL_GefKW, demandCount, IncreaseMode.Replace); }

                else if (demand.Contains("wasserwerfer")) { IncreaseDemand(VehicleClass.POL_WaWe, demandCount, IncreaseMode.Replace); }

                else if (demand.Contains("mek-fahrzeug")) { IncreaseDemand(VehicleClass.POL_MEK, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("sek-fahrzeug")) { IncreaseDemand(VehicleClass.POL_SEK, demandCount, IncreaseMode.Replace); }

                else if (demand.Contains("polizisten")) { IncreaseDemand(VehicleClass.PROC_POL_MEN, demandCount, IncreaseMode.Replace); }

                #endregion
                #region THW

                else if (demand.Contains("thw-einsatzleitung")) { IncreaseDemand(VehicleClass.THW_MTW_TZ, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("gerätekraftwagen")) { IncreaseDemand(VehicleClass.THW_GKW, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("mzkw")) { IncreaseDemand(VehicleClass.THW_MzKW, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("anhänger drucklufterzeugung")) { IncreaseDemand(VehicleClass.THW_DLE, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("radlader")) { IncreaseDemand(VehicleClass.THW_BRmGR, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("lkw kipper")) { IncreaseDemand(VehicleClass.THW_K9, demandCount, IncreaseMode.Replace); }

                #endregion
                #region  Wasserrettung

                else if (demand.Contains("boot")) { IncreaseDemand(VehicleClass.WR_Boot, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("gw-taucher")) { IncreaseDemand(VehicleClass.WR_GW_Taucher, demandCount, IncreaseMode.Replace); }

                else if (demand.Contains("person mit gw-wasserrettung ausbildung")) { IncreaseDemand(VehicleClass.PROC_WR_MEN, demandCount, IncreaseMode.Replace); }
                else if (demand.Contains("personen mit gw-wasserrettung ausbildung")) { IncreaseDemand(VehicleClass.PROC_WR_MEN, demandCount, IncreaseMode.Replace); }

                #endregion

                else
                {

                    Print.Error("MissionMissing/AddMissingText (Fehlende Übersetzung)", demand);
                    return;

                }
                IsEmpty = false;

            }
            
        }
        public void AddPatient(Patient patient, bool isKTW)
        {

            bool alerted = false;
                        
            //RTW
            if (patient.MissingText.Contains("RTW"))
            {
                IsEmpty = false;
                alerted = true;

                IncreaseDemand(VehicleClass.RD_RTW, 1, IncreaseMode.Add);
                SetClassFlexible(VehicleClass.RD_RTW);
            }

            //NEF
            if (patient.MissingText.Contains("NEF"))
            {
                IsEmpty = false;
                alerted = true;

                IncreaseDemand(VehicleClass.RD_NEF, 1, IncreaseMode.Add);
                SetClassFlexible(VehicleClass.RD_NEF);
            }

            //RTH
            if (patient.MissingText.Contains("RTH"))
            {
                IsEmpty = false;
                alerted = true;

                IncreaseDemand(VehicleClass.RD_RTH, 1, IncreaseMode.Add);
                SetClassFlexible(VehicleClass.RD_RTH);
            }

            //LNA & OrgL werden separat geschickt
            if (patient.MissingText.Contains("LNA"))
            {
                IsEmpty = false;
                alerted = true;

                IncreaseDemand(VehicleClass.RD_LNA, 1, IncreaseMode.Replace);
            }
            if (patient.MissingText.Contains("OrgL"))
            {
                IsEmpty = false;
                alerted = true;

                IncreaseDemand(VehicleClass.RD_OrgL, 1, IncreaseMode.Replace);
            }


            //Tragehilfe
            if (patient.MissingText.Contains("Tragehilfe"))
            {
                alerted = true;

                AddSingleLF();
            }

            //Wenn kein Fahrzeug vor Ort --> RTW schicken // Wenn KTW-Einsatz --> KTW schicken
            if (patient.MissingText == "null" && patient.LivePercentage == 100)
            {
                IsEmpty = false;
                alerted = true;

                if (isKTW) { IncreaseDemand(VehicleClass.RD_KTW, 1, IncreaseMode.Add); SetClassFlexible(VehicleClass.RD_KTW); }
                else { IncreaseDemand(VehicleClass.RD_RTW, 1, IncreaseMode.Add); SetClassFlexible(VehicleClass.RD_RTW); }
            }

            //Wenn unbehandelt
            if (!alerted && patient.MissingText != "null")
            {
                Print.Error("MissionMissing/AddPatient (Kein Fahrzeug alarmiert)", patient.MissingText);
            }

        }

        public void SetPrisoners(int count) { if (count > 0) { IsEmpty = false; IncreaseDemand(VehicleClass.PROC_POL_PRISONERS, count, IncreaseMode.Replace); } }

        //#########################################################################################

        public enum IncreaseMode
        {
            Add,
            Replace
        }

        public void IncreaseDemand(VehicleClass c, int amount, IncreaseMode mode)
        {
            if(MissingClasses == null) { MissingClasses = new SortedDictionary<VehicleClass, int>(); }

            IsEmpty = false;
            if(MissingClasses.ContainsKey(c))
            {
                if(mode == IncreaseMode.Add) { MissingClasses[c] += amount; }
                else { MissingClasses[c] = amount; }
            }
            else
            {
                MissingClasses.Add(c, amount);
            }
        }

        public void SetClassFlexible(VehicleClass c)
        {
            if(!FlexibleDemand.Contains(c)) { FlexibleDemand.Add(c); }
        }

        public void AddSingleLF()
        {
            IncreaseDemand(VehicleClass.FW_LF, 1, IncreaseMode.Replace);
        }
        public void AddSingleFuStW()
        {
            if (!MissingClasses.ContainsKey(VehicleClass.PROC_POL_PRISONERS)) { return; }

            IncreaseDemand(VehicleClass.POL_FuStW, MissingClasses[VehicleClass.PROC_POL_PRISONERS], IncreaseMode.Add);
        }
        public void AddSingleRTW()
        {
            IncreaseDemand(VehicleClass.RD_RTW, 1, IncreaseMode.Add);
        }

        //#########################################################################################

        public void ReduceDemand(Vehicle v)
        {

            //Fahrzeugklassen
            var toReduce = Vehicle.GetVehicleClassForType(v.Type);
            foreach (var c in toReduce)
            {
                DecreaseDemand(c, 1);
            }

            //Prozedurklassen
            if (v.Organisation == VehicleOrg.FeuerWehr)
            {
                DecreaseDemand(VehicleClass.PROC_FW_WATER, v.WaterAmount);
                DecreaseDemand(VehicleClass.PROC_FW_MEN, v.MenAmount);
            }
            if (v.Type == VehicleType.WR_GW_Wasserrettung) { DecreaseDemand(VehicleClass.PROC_WR_MEN, v.MenAmount); }

        }

        public void ClearDemand(VehicleClass c)
        {
            if (MissingClasses.ContainsKey(c))
            {
                MissingClasses.Remove(c);
            }
        }

        private void DecreaseDemand(VehicleClass c, int amount)
        {
            if (MissingClasses.ContainsKey(c))
            {
                int current = MissingClasses[c];
                current -= amount; if (current < 0) { current = 0; }
                MissingClasses[c] = current;
            }
        }

    }

    //#########################################################################################

    private class Vehicle
    {

        public string ID { get; private set; }
        public string Title { get; private set; }
        public string AssignedMissionID { get; private set; }

        public VehicleOrg Organisation => GetVehicleOrg(Type);
        public VehicleType Type { get; private set; } = VehicleType.UNSET;

        public int MenAmount { get; private set; } = -1;

        public string Station { get; private set; }

        public int FMS { get; private set; } = -1;
        public bool IsAvailable => (FMS == 2 || FMS == 1);
        public bool IsAvailableAtStation => FMS == 2;

        //Anhänger
        public bool IsTrailer { get; private set; } = false;
        public bool IsTractor { get; private set; } = false;

        public bool IsTractorAvailable { get; private set; }
        public bool IsLinkedTrailer => !string.IsNullOrEmpty(LinkedTractorID);
        public string LinkedTractorID { get; private set; }
        
        //Feuerwehr
        public int WaterAmount { get; private set; } = -1;

        //Rettungsdienst
        public bool HasPatient { get; private set; } = false;

        //#########################################################################################

        public Vehicle(string id, int realMenAmount) { ID = id; MenAmount = realMenAmount; }
        public Vehicle(string id, string title, string typeRaw, int water, string station, int fms,bool isTrailer,bool isTractorAvailable,string linkedTractorId, bool haspat, string debug)
        {
            ID = id;
            Title = title;
            Station = station;
            FMS = fms;
            WaterAmount = water;
            MenAmount = 0;

            IsTrailer = isTrailer;
            IsTractorAvailable = isTractorAvailable;
            LinkedTractorID = linkedTractorId;

            HasPatient = haspat;

            //##############################Convert classRaw

            if (string.IsNullOrEmpty(typeRaw)) { return; }
            GetVehicleTypeForRaw(typeRaw);

        }

        //#########################################################################################

        public void AssignMission(string missionId) { AssignedMissionID = missionId; }
        public void AssignMission() { AssignMission(string.Empty); }

        //#########################################################################################

        public void Merge(Vehicle vehicle)
        {
            if (string.IsNullOrEmpty(vehicle.AssignedMissionID)) { AssignedMissionID = vehicle.AssignedMissionID; }

            if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(vehicle.Title)) { Title = vehicle.Title; }
            if (Type < 0 && vehicle.Type >= 0) { Type = vehicle.Type; }
            if (string.IsNullOrWhiteSpace(Station) && !string.IsNullOrWhiteSpace(vehicle.Station)) { Station = vehicle.Station; }
            if (FMS != vehicle.FMS && vehicle.FMS > 0) { FMS = vehicle.FMS; }
            if (WaterAmount != vehicle.WaterAmount && vehicle.WaterAmount >= 0) { WaterAmount = vehicle.WaterAmount; }
            if (MenAmount > vehicle.MenAmount && vehicle.MenAmount > 0) { MenAmount = vehicle.MenAmount; }
        }

        public void SetFMS(int fms) { FMS = fms; }

        //#########################################################################################

        private static VehicleOrg GetVehicleOrg(VehicleType t)
        {

            if ((int)t >= 0 && (int)t <= 99) { return VehicleOrg.FeuerWehr; }
            if ((int)t >= 100 && (int)t <= 199) { return VehicleOrg.RettungsDienst; }
            if ((int)t >= 200 && (int)t <= 299) { return VehicleOrg.Polizei; }
            if ((int)t >= 300 && (int)t <= 399) { return VehicleOrg.TechnHilfsWerk; }
            if ((int)t >= 400 && (int)t <= 499) { return VehicleOrg.WasserRettung; }

            return VehicleOrg.NONE;

        }

        //#########################################################################################

        public static List<VehicleType> GetVehicleTypesForClass(VehicleClass c)
        {

            List<VehicleType> typeList = new List<VehicleType>();
            switch (c)
            {

                //Fahrzeugklassen übersetzen
                #region Feuerwehr

                case VehicleClass.FW_LF:
                    typeList.Add(VehicleType.FW_LF);
                    typeList.Add(VehicleType.FW_HLF);
                    break;

                case VehicleClass.FW_Rüst:
                    typeList.Add(VehicleType.FW_Rüst);
                    typeList.Add(VehicleType.FW_HLF);
                    typeList.Add(VehicleType.FW_AB_Rüst);
                    break;

                case VehicleClass.FW_ELW1:
                    typeList.Add(VehicleType.FW_ELW1);
                    typeList.Add(VehicleType.FW_ELW2);
                    typeList.Add(VehicleType.FW_AB_ELW);
                    break;

                case VehicleClass.FW_ELW2:
                    typeList.Add(VehicleType.FW_ELW2);
                    typeList.Add(VehicleType.FW_AB_ELW);
                    break;

                case VehicleClass.FW_DLK:
                    typeList.Add(VehicleType.FW_DLK);
                    typeList.Add(VehicleType.FW_TM);
                    break;

                case VehicleClass.FW_Kran:
                    typeList.Add(VehicleType.FW_Kran);
                    break;

                case VehicleClass.FW_GW_DekonP:
                    typeList.Add(VehicleType.FW_GW_DekonP);
                    typeList.Add(VehicleType.FW_AB_DekonP);
                    break;

                case VehicleClass.FW_GW_Atemschutz:
                    typeList.Add(VehicleType.FW_GW_Atemschutz);
                    typeList.Add(VehicleType.FW_AB_Atemschutz);
                    break;

                case VehicleClass.FW_Schlauchwagen:
                    typeList.Add(VehicleType.FW_Schlauchwagen);
                    typeList.Add(VehicleType.FW_AB_Schlauch);
                    break;

                case VehicleClass.FW_GW_Öl:
                    typeList.Add(VehicleType.FW_GW_Öl);
                    typeList.Add(VehicleType.FW_AB_Öl);
                    break;

                case VehicleClass.FW_GW_Messtechnik:
                    typeList.Add(VehicleType.FW_GW_Messtechnik);
                    break;

                case VehicleClass.FW_GW_Gefahrgut:
                    typeList.Add(VehicleType.FW_GW_Gefahrgut);
                    typeList.Add(VehicleType.FW_AB_Gefahrgut);
                    break;

                case VehicleClass.FW_GW_Höhenrettung:
                    typeList.Add(VehicleType.FW_GW_Höhenrettung);
                    break;

                case VehicleClass.FW_FlugfeldLF:
                    typeList.Add(VehicleType.FW_FlugfeldLF);
                    break;

                case VehicleClass.FW_FlugfeldTreppe:
                    typeList.Add(VehicleType.FW_FlugfeldTreppe);
                    break;

                case VehicleClass.FW_GW_Werkfeuerwehr:
                    typeList.Add(VehicleType.FW_GW_Werkfeuerwehr);
                    break;

                case VehicleClass.FW_ULF:
                    typeList.Add(VehicleType.FW_ULF);
                    break;

                case VehicleClass.FW_TM:
                    typeList.Add(VehicleType.FW_TM);
                    break;

                case VehicleClass.FW_Turbolöscher:
                    typeList.Add(VehicleType.FW_Turbolöscher);
                    break;

                #endregion
                #region Rettungsdienst

                case VehicleClass.RD_NEF:
                    typeList.Add(VehicleType.RD_GRTW);
                    typeList.Add(VehicleType.RD_NEF);
                    typeList.Add(VehicleType.RD_NAW);
                    typeList.Add(VehicleType.RD_RTH);
                    break;
                case VehicleClass.RD_RTW:
                    typeList.Add(VehicleType.RD_GRTW);
                    typeList.Add(VehicleType.RD_RTW);
                    typeList.Add(VehicleType.RD_NAW);
                    break;
                case VehicleClass.RD_KTW:
                    typeList.Add(VehicleType.RD_KTW);
                    break;
                case VehicleClass.RD_LNA:
                    typeList.Add(VehicleType.RD_LNA);
                    break;
                case VehicleClass.RD_OrgL:
                    typeList.Add(VehicleType.RD_OrgL);
                    break;
                case VehicleClass.RD_RTH:
                    typeList.Add(VehicleType.RD_RTH);
                    break;
                case VehicleClass.RD_GW_SAN:
                    typeList.Add(VehicleType.RD_GW_SAN);
                    break;
                case VehicleClass.RD_ELW_SAN:
                    typeList.Add(VehicleType.RD_ELW_SEG);
                    break;
                        
                #endregion
                #region Polizei

                case VehicleClass.POL_FuStW:
                    typeList.Add(VehicleType.POL_FuStW);
                    break;
                case VehicleClass.POL_FüKW:
                    typeList.Add(VehicleType.POL_FüKW);
                    break;
                case VehicleClass.POL_leBefKW:
                    typeList.Add(VehicleType.POL_leBefKW);
                    break;
                case VehicleClass.POL_GruKW:
                    typeList.Add(VehicleType.POL_GruKW);
                    break;
                case VehicleClass.POL_GefKW:
                    typeList.Add(VehicleType.POL_GefKW);
                    break;
                case VehicleClass.POL_WaWe:
                    typeList.Add(VehicleType.POL_WaWe);
                    break;
                case VehicleClass.POL_Hubschrauber:
                    typeList.Add(VehicleType.POL_Hubschrauber);
                    break;
                case VehicleClass.POL_SEK:
                    typeList.Add(VehicleType.POL_SEK);
                    break;
                case VehicleClass.POL_MEK:
                    typeList.Add(VehicleType.POL_MEK);
                    break;

                #endregion
                #region THW

                case VehicleClass.THW_GKW:
                    typeList.Add(VehicleType.THW_GKW);
                    break;
                case VehicleClass.THW_MzKW:
                    typeList.Add(VehicleType.THW_MzKW);
                    break;
                case VehicleClass.THW_K9:
                    typeList.Add(VehicleType.THW_K9);
                    break;
                case VehicleClass.THW_BRmGR:
                    typeList.Add(VehicleType.THW_A_BRmGR);
                    break;
                case VehicleClass.THW_DLE:
                    typeList.Add(VehicleType.THW_A_DLE);
                    break;
                case VehicleClass.THW_MTW_TZ:
                    typeList.Add(VehicleType.THW_MTW_TZ);
                    break;

                #endregion
                #region WasserRettung

                case VehicleClass.WR_GW_Taucher:
                    typeList.Add(VehicleType.WR_GW_Taucher);
                    typeList.Add(VehicleType.THW_TKW);
                    break;

                case VehicleClass.WR_Boot:
                    typeList.Add(VehicleType.THW_A_Boot);
                    typeList.Add(VehicleType.WR_A_MzB);
                    typeList.Add(VehicleType.FW_AB_MzB);
                    break;

                #endregion

                //Prozedurklassen überspringen
                case VehicleClass.PROC_FW_MEN:
                case VehicleClass.PROC_FW_WATER:
                case VehicleClass.PROC_POL_PRISONERS:
                case VehicleClass.PROC_POL_MEN:
                case VehicleClass.PROC_WR_MEN:
                    typeList.Add(VehicleType.UNSET);
                    break;

                //Sonst --> Fehler
                default:
                    typeList.Add(VehicleType.UNSET);
                    Print.Error("Vehicle/GetVehicleTypesForClass // Fehlende Umwandlung von Klasse zu Typ ", c.ToString());
                    break;

            }
            return typeList;

        }
        public static List<VehicleClass> GetVehicleClassForType(VehicleType t)
        {

            List<VehicleClass> classList = new List<VehicleClass>();
            switch (t)
            {

                #region Feuerwehr

                case VehicleType.FW_LF:
                    classList.Add(VehicleClass.FW_LF);
                    break;
                case VehicleType.FW_AB_Rüst:
                case VehicleType.FW_Rüst:
                    classList.Add(VehicleClass.FW_Rüst);
                    break;
                case VehicleType.FW_HLF:
                    classList.Add(VehicleClass.FW_LF);
                    classList.Add(VehicleClass.FW_Rüst);
                    break;
                case VehicleType.FW_ELW1:
                    classList.Add(VehicleClass.FW_ELW1);
                    break;
                case VehicleType.FW_AB_ELW:
                case VehicleType.FW_ELW2:
                    classList.Add(VehicleClass.FW_ELW2);
                    classList.Add(VehicleClass.FW_ELW1);
                    break;
                case VehicleType.FW_DLK:
                    classList.Add(VehicleClass.FW_DLK);
                    break;
                case VehicleType.FW_Kran:
                    classList.Add(VehicleClass.FW_Kran);
                    break;
                case VehicleType.FW_GW_DekonP:
                case VehicleType.FW_AB_DekonP:
                    classList.Add(VehicleClass.FW_GW_DekonP);
                    break;
                case VehicleType.FW_GW_Atemschutz:
                case VehicleType.FW_AB_Atemschutz:
                    classList.Add(VehicleClass.FW_GW_Atemschutz);
                    break;
                case VehicleType.FW_Schlauchwagen:
                case VehicleType.FW_AB_Schlauch:
                    classList.Add(VehicleClass.FW_Schlauchwagen);
                    break;
                case VehicleType.FW_GW_Öl:
                case VehicleType.FW_AB_Öl:
                    classList.Add(VehicleClass.FW_GW_Öl);
                    break;
                case VehicleType.FW_GW_Messtechnik:
                    classList.Add(VehicleClass.FW_GW_Messtechnik);
                    break;
                case VehicleType.FW_GW_Gefahrgut:
                case VehicleType.FW_AB_Gefahrgut:
                    classList.Add(VehicleClass.FW_GW_Gefahrgut);
                    break;
                case VehicleType.FW_GW_Höhenrettung:
                    classList.Add(VehicleClass.FW_GW_Höhenrettung);
                    break;
                case VehicleType.FW_AB_MzB:
                    classList.Add(VehicleClass.WR_Boot);
                    break;
                case VehicleType.FW_FlugfeldLF:
                    classList.Add(VehicleClass.FW_FlugfeldLF);
                    break;
                case VehicleType.FW_FlugfeldTreppe:
                    classList.Add(VehicleClass.FW_FlugfeldTreppe);
                    break;
                case VehicleType.FW_GW_Werkfeuerwehr:
                    classList.Add(VehicleClass.FW_GW_Werkfeuerwehr);
                    break;
                case VehicleType.FW_ULF:
                    classList.Add(VehicleClass.FW_ULF);
                    break;
                case VehicleType.FW_TM:
                    classList.Add(VehicleClass.FW_TM);
                    classList.Add(VehicleClass.FW_DLK);
                    break;
                case VehicleType.FW_Turbolöscher:
                    classList.Add(VehicleClass.FW_Turbolöscher);
                    break;

                case VehicleType.FW_MTW:
                case VehicleType.FW_WLF:
                    classList.Add(VehicleClass.NoDemand);
                    break;

                #endregion
                #region Rettungsdienst

                case VehicleType.RD_KTW:
                    classList.Add(VehicleClass.RD_KTW);
                    break;
                case VehicleType.RD_RTW:
                    classList.Add(VehicleClass.RD_RTW);
                    classList.Add(VehicleClass.RD_KTW);
                    break;
                case VehicleType.RD_NEF:
                    classList.Add(VehicleClass.RD_NEF);
                    break;
                case VehicleType.RD_LNA:
                    classList.Add(VehicleClass.RD_LNA);
                    break;
                case VehicleType.RD_OrgL:
                    classList.Add(VehicleClass.RD_OrgL);
                    break;
                case VehicleType.RD_RTH:
                    classList.Add(VehicleClass.RD_RTH);
                    classList.Add(VehicleClass.RD_NEF);
                    break;
                case VehicleType.RD_NAW:
                    classList.Add(VehicleClass.RD_NEF);
                    classList.Add(VehicleClass.RD_RTW);
                    break;

                case VehicleType.RD_ELW_SEG:
                case VehicleType.RD_GRTW:
                    classList.Add(VehicleClass.NoDemand);
                    break;

                #endregion
                #region Polizei

                case VehicleType.POL_FuStW:
                    classList.Add(VehicleClass.POL_FuStW);
                    break;
                case VehicleType.POL_FüKW:
                    classList.Add(VehicleClass.POL_FüKW);
                    break;
                case VehicleType.POL_leBefKW:
                    classList.Add(VehicleClass.POL_leBefKW);
                    break;
                case VehicleType.POL_GruKW:
                    classList.Add(VehicleClass.POL_GruKW);
                    break;
                case VehicleType.POL_GefKW:
                    classList.Add(VehicleClass.POL_GefKW);
                    break;
                case VehicleType.POL_WaWe:
                    classList.Add(VehicleClass.POL_WaWe);
                    break;
                case VehicleType.POL_Hubschrauber:
                    classList.Add(VehicleClass.POL_Hubschrauber);
                    break;
                case VehicleType.POL_SEK:
                    classList.Add(VehicleClass.POL_SEK);
                    break;
                case VehicleType.POL_MEK:
                    classList.Add(VehicleClass.POL_MEK);
                    break;

                #endregion
                #region THW

                case VehicleType.THW_GKW:
                    classList.Add(VehicleClass.THW_GKW);
                    classList.Add(VehicleClass.FW_Rüst);
                    break;
                case VehicleType.THW_MzKW:
                    classList.Add(VehicleClass.THW_MzKW);
                    break;
                case VehicleType.THW_MTW_TZ:
                    classList.Add(VehicleClass.THW_MTW_TZ);
                    break;
                case VehicleType.THW_K9:
                    classList.Add(VehicleClass.THW_K9);
                    break;
                case VehicleType.THW_A_BRmGR:
                    classList.Add(VehicleClass.THW_BRmGR);
                    break;
                case VehicleType.THW_TKW:
                    classList.Add(VehicleClass.WR_GW_Taucher);
                    break;
                case VehicleType.THW_A_DLE:
                    classList.Add(VehicleClass.THW_DLE);
                    break;
                case VehicleType.THW_A_Boot:
                    classList.Add(VehicleClass.WR_Boot);
                    break;

                case VehicleType.THW_MLW5:
                case VehicleType.THW_LdKr:
                    classList.Add(VehicleClass.NoDemand);
                    break;

                #endregion
                #region WasserRettung

                case VehicleType.WR_A_MzB:
                    classList.Add(VehicleClass.WR_Boot);
                    break;
                case VehicleType.WR_GW_Taucher:
                    classList.Add(VehicleClass.WR_GW_Taucher);
                    break;
                case VehicleType.WR_GW_Wasserrettung:
                    classList.Add(VehicleClass.NoDemand);
                    break;

                #endregion

                default:
                    classList.Add(VehicleClass.UNSET);
                    Print.Error("Vehicle/GetVehicleTypesForClass // Fehlende Umwandlung von Typ zu Klasse ", t.ToString());
                    break;

            }
            return classList;

        }

        public void GetVehicleTypeForRaw(string typeRaw)
        {

            switch (typeRaw.ToLower())
            {

                #region Feuerwehr

                case "hlf 10":
                case "hlf 20":
                    MenAmount = 9;
                    Type = VehicleType.FW_HLF;
                    return;

                case "lf 10":
                case "lf 20":
                case "lf 8/6":
                case "lf 20/16":
                case "lf 10/6":
                case "lf 16-ts":
                    MenAmount = 9;
                    Type = VehicleType.FW_LF;
                    return;

                case "tsf-w":
                case "klf":
                case "mlf":
                case "tlf 16/25":
                    MenAmount = 6;
                    Type = VehicleType.FW_LF;
                    return;

                case "tlf 2000":
                case "tlf 3000":
                case "tlf 8/8":
                case "tlf 8/18":
                case "tlf 16/24-tr":
                case "tlf 16/45":
                case "tlf 20/40":
                case "tlf 20/40-sl":
                case "tlf 16":
                case "tlf 4000":
                    MenAmount = 3;
                    Type = VehicleType.FW_LF;
                    return;

                case "gw-l2-wasser":
                case "sw 1000":
                case "sw 2000-tr":
                case "sw kats":
                    MenAmount = 3;
                    Type = VehicleType.FW_Schlauchwagen;
                    return;

                case "sw 2000":
                    MenAmount = 6;
                    Type = VehicleType.FW_Schlauchwagen;
                    return;

                case "dlk 23":
                    MenAmount = 3;
                    Type = VehicleType.FW_DLK;
                    return;

                case "rw":
                    MenAmount = 3;
                    Type = VehicleType.FW_Rüst;
                    return;

                case "elw 1":
                    MenAmount = 3;
                    Type = VehicleType.FW_ELW1;
                    return;

                case "elw 2":
                    MenAmount = 6;
                    Type = VehicleType.FW_ELW2;
                    return;

                case "gw-a":
                    MenAmount = 3;
                    Type = VehicleType.FW_GW_Atemschutz;
                    return;

                case "gw-öl":
                    MenAmount = 3;
                    Type = VehicleType.FW_GW_Öl;
                    return;

                case "gw-messtechnik":
                    MenAmount = 3;
                    Type = VehicleType.FW_GW_Messtechnik;
                    return;

                case "gw-gefahrgut":
                    MenAmount = 3;
                    Type = VehicleType.FW_GW_Gefahrgut;
                    return;

                case "gw-höhenrettung":
                    MenAmount = 9;
                    Type = VehicleType.FW_GW_Höhenrettung;
                    return;

                case "mtw":
                    MenAmount = 9;
                    Type = VehicleType.FW_MTW;
                    return;

                case "wlf":
                    MenAmount = 3;
                    Type = VehicleType.FW_WLF;
                    IsTractor = true;
                    return;

                case "dekon-p":
                    MenAmount = 6;
                    Type = VehicleType.FW_GW_DekonP;
                    return;

                case "fwk":
                    MenAmount = 2;
                    Type = VehicleType.FW_Kran;
                    return;

                case "ab-rüst":
                    MenAmount = 0;
                    Type = VehicleType.FW_AB_Rüst;
                    IsTrailer = true;
                    return;

                case "ab-atemschutz":
                    MenAmount = 0;
                    Type = VehicleType.FW_AB_Atemschutz;
                    IsTrailer = true;
                    return;

                case "ab-öl":
                    MenAmount = 0;
                    Type = VehicleType.FW_AB_Öl;
                    IsTrailer = true;
                    return;

                case "ab-dekon-p":
                    MenAmount = 0;
                    Type = VehicleType.FW_AB_DekonP;
                    IsTrailer = true;
                    return;

                case "ab-schlauch":
                    MenAmount = 0;
                    Type = VehicleType.FW_AB_Schlauch;
                    IsTrailer = true;
                    return;

                case "ab-mzb":
                    MenAmount = 0;
                    Type = VehicleType.FW_AB_MzB;
                    IsTrailer = true;
                    return;

                case "ab-gefahrgut":
                    MenAmount = 0;
                    Type = VehicleType.FW_AB_Gefahrgut;
                    IsTrailer = true;
                    return;

                case "ab-einsatzleitung":
                    MenAmount = 0;
                    Type = VehicleType.FW_AB_ELW;
                    IsTrailer = true;
                    return;

                case "flf":
                    MenAmount = 3;
                    Type = VehicleType.FW_FlugfeldLF;
                    return;

                case "rettungstreppe":
                    MenAmount = 2;
                    Type = VehicleType.FW_FlugfeldTreppe;
                    return;

                case "TODO: WF-GW":
                    MenAmount = 9;
                    Type = VehicleType.FW_GW_Werkfeuerwehr;
                    return;

                case "TODO: ULF":
                    MenAmount = 3;
                    Type = VehicleType.FW_ULF;
                    return;

                case "TODO: TM50":
                    MenAmount = 3;
                    Type = VehicleType.FW_TM;
                    return;

                case "TODO: TL":
                    MenAmount = 3;
                    Type = VehicleType.FW_Turbolöscher;
                    return;

                #endregion
                #region Rettungsdienst

                case "nef":
                    MenAmount = 2;
                    Type = VehicleType.RD_NEF;
                    return;

                case "rtw":
                    MenAmount = 2;
                    Type = VehicleType.RD_RTW;
                    return;

                case "ktw typ b":
                case "ktw":
                    MenAmount = 2;
                    Type = VehicleType.RD_KTW;
                    return;

                case "kdow-lna":
                    MenAmount = 1;
                    Type = VehicleType.RD_LNA;
                    HasPatient = true;
                    return;

                case "kdow-orgl":
                    MenAmount = 1;
                    Type = VehicleType.RD_OrgL;
                    HasPatient = true;
                    return;

                case "grtw":
                    MenAmount = 6;
                    Type = VehicleType.RD_GRTW;
                    return;

                case "naw":
                    MenAmount = 3;
                    Type = VehicleType.RD_NAW;
                    return;

                case "rth":
                    MenAmount = 1;
                    Type = VehicleType.RD_RTH;
                    return;

                case "gw-san":
                    MenAmount = 6;
                    Type = VehicleType.RD_GW_SAN;
                    return;

                case "elw 1 (seg)":
                    MenAmount = 2;
                    Type = VehicleType.RD_ELW_SEG;
                    return;

                #endregion
                #region Polizei

                case "fustw":
                    MenAmount = 2;
                    Type = VehicleType.POL_FuStW;
                    return;
                case "polizeihubschrauber":
                    MenAmount = 1;
                    Type = VehicleType.POL_Hubschrauber;
                    return;
                case "fükw":
                    MenAmount = 3;
                    Type = VehicleType.POL_FüKW;
                    return;
                case "lebefkw":
                    MenAmount = 3;
                    Type = VehicleType.POL_leBefKW;
                    return;
                case "grukw":
                    MenAmount = 9;
                    Type = VehicleType.POL_GruKW;
                    return;
                case "gefkw":
                    MenAmount = 2;
                    Type = VehicleType.POL_GefKW;
                    return;
                case "wawe 10":
                    MenAmount = 5;
                    Type = VehicleType.POL_WaWe;
                    return;
                case "sek - zf":
                    MenAmount = 4;
                    Type = VehicleType.POL_SEK;
                    return;
                case "sek - mtf":
                    MenAmount = 9;
                    Type = VehicleType.POL_SEK;
                    return;
                case "mek - zf":
                    MenAmount = 4;
                    Type = VehicleType.POL_MEK;
                    return;
                case "mek - mtf":
                    MenAmount = 9;
                    Type = VehicleType.POL_MEK;
                    return;

                #endregion
                #region THW

                case "gkw":
                    MenAmount = 9;
                    Type = VehicleType.THW_GKW;
                    IsTractor = true; //für a-dle
                    return;
                case "mzkw":
                    MenAmount = 9;
                    Type = VehicleType.THW_MzKW;
                    IsTractor = true; //für a-dle
                    return;
                case "mtw-tz":
                    MenAmount = 4;
                    Type = VehicleType.THW_MTW_TZ;
                    return;
                case "lkw k 9":
                    MenAmount = 3;
                    Type = VehicleType.THW_K9;
                    IsTractor = true; //zieht brmgr
                    return;
                case "mlw 5":
                    MenAmount = 6;
                    Type = VehicleType.THW_MLW5;
                    IsTractor = true; //für a-dle
                    return;
                case "brmg r":
                    Type = VehicleType.THW_A_BRmGR;
                    IsTrailer = true; //benötigt k9
                    return;
                case "anh dle":
                    Type = VehicleType.THW_A_DLE;
                    IsTrailer = true; //benötigt mlw, gkw, mzkw
                    return;
                case "anh mzb":
                case "anh schlb":
                case "anh mzab":
                    Type = VehicleType.THW_A_Boot;
                    IsTrailer = true; //benötigt lkr
                    return;
                case "lkw 7 lkr 19 tm":
                    MenAmount = 2;
                    Type = VehicleType.THW_LdKr;
                    IsTractor = true; //für a-mzab, a-mzb, a-sb
                    return;
                case "tauchkraftwagen":
                    MenAmount = 2;
                    Type = VehicleType.THW_TKW;
                    return;

                #endregion
                #region Wasserrettung

                case "mzb":
                    MenAmount = 0;
                    Type = VehicleType.WR_A_MzB;
                    IsTrailer = true;
                    return;

                case "gw-wasserrettung":
                    MenAmount = 6;
                    Type = VehicleType.WR_GW_Wasserrettung;
                    IsTractor = true; //für mzb
                    return;

                case "gw-taucher":
                    MenAmount = 2;
                    Type = VehicleType.WR_GW_Taucher;
                    IsTractor = true; //für mzb
                    return;

                #endregion

            }

            Print.Error("Vehicle/GetVehicleForTypeRaw", typeRaw.ToLower());

        }

        //#########################################################################################

        public static List<Vehicle> Get(List<Vehicle> source, List<string> forbiddenIDs, VehicleClass c)
        {
            return Get(source, forbiddenIDs, new List<VehicleClass>() { c });
        }
        public static List<Vehicle> Get(List<Vehicle> source, List<string> forbiddenIDs, List<VehicleClass> classes)
        {
            List<Vehicle> result = new List<Vehicle>();
            foreach (var c in classes)
            {
                foreach (var type in GetVehicleTypesForClass(c))
                {
                    result.AddRange((from x in source where x.Type == type && !forbiddenIDs.Contains(x.ID) select x));
                }
            }
            return result;
        }

    }

    private enum VehicleType
    {

        UNSET = -1,

        FW_DLK = 0,         //Drehleiter
        FW_ELW1,            //Einsatzleitwagen 1 bzw. Kommandowagen
        FW_ELW2,            //Einsatzleitwagen 2
        FW_AB_ELW,
        FW_Kran,            //Feuerwehrkran
        FW_FlugfeldLF,      //Flugfeldlöschfahrzeug
        FW_FlugfeldTreppe,  //Rettungstreppe
        FW_GW_Atemschutz,   //Gerätewagen
        FW_AB_Atemschutz,   //Abrollbehälter
        FW_GW_DekonP,
        FW_AB_DekonP,
        FW_GW_Gefahrgut,
        FW_AB_Gefahrgut,
        FW_GW_Höhenrettung,
        FW_GW_Messtechnik,
        FW_GW_Öl,
        FW_AB_Öl,
        FW_GW_Werkfeuerwehr,
        FW_HLF,
        FW_LF,              //Löschfahrzeug
        FW_Rüst,            //Rüstwagen
        FW_AB_Rüst,
        FW_Schlauchwagen,
        FW_AB_Schlauch,
        FW_TM,              //Teleskopmast
        FW_Turbolöscher,
        FW_ULF,             //Universallöschfahrzeug m. Löscharm
        FW_AB_MzB,          //Mehrzweckboot
        FW_WLF,             //Wechsellader
        FW_MTW,             //Mannschaftstransportwagen / ohne Funktion

        RD_GW_SAN = 100,    //Gerätewagen Sanitätsdienst (MANV-Zelt)
        RD_ELW_SEG,
        RD_KTW,
        RD_RTW,
        RD_NEF,
        RD_RTH,
        RD_LNA,
        RD_OrgL,
        RD_GRTW,            //Großraum-RTW
        RD_NAW,

        POL_FuStW = 200,    //Streifenwagen
        POL_FüKW,           //Führungskraftwagen (Abteilungsführer)
        POL_leBefKW,        //Leichter Befehlskraftwagen (Zugführer)
        POL_GruKW,          //Gruppenkraftwagen
        POL_GefKW,          //Gefangenenkraftwagen
        POL_WaWe,           //Wasserwerfer
        POL_Hubschrauber,
        POL_MEK,            //ZF & MTF
        POL_SEK,            //ZF & MTF

        THW_GKW = 300,      //Gerätekraftwagen
        THW_MzKW,           //Mehrzweckkraftwagen
        THW_MTW_TZ,         //Mannschaftstransportwagen-Technischer Zug
        THW_K9,             //Kipper 9t - Zugfahrzeug für 
        THW_MLW5,           //Mannschaftslastwagen - Äqu. MTW bei FW
        THW_A_BRmGR,        //Berge-Räum-Radlader
        THW_A_DLE,          //Anhänger Druckluft
        THW_A_Boot,         //MzB, MzAB, Schlauchboot
        THW_LdKr,           //Ladekran
        THW_TKW,            //Tauchkraftwagen

        WR_A_MzB = 400,     //Mehrzweckboot
        WR_GW_Taucher,      //Wasserrettung
        WR_GW_Wasserrettung //Wasserrettung-Zugfahrzeug für Boot

    }
    private enum VehicleClass
    {

        UNSET = -1,

        //Fahrzeugklassen
        FW_ELW2 = 0,        //Einsatzleitwagen 2
        FW_ELW1,            //Einsatzleitwagen 1 bzw. Kommandowagen
        FW_LF,              //Löschfahrzeug
        FW_Rüst,            //Rüstwagen
        FW_DLK,             //Drehleiter
                
        FW_GW_Atemschutz,   //Gerätewagen
        FW_GW_DekonP,
        FW_GW_Gefahrgut,
        FW_GW_Höhenrettung,
        FW_GW_Messtechnik,
        FW_GW_Öl,
        FW_GW_Werkfeuerwehr,
        FW_Schlauchwagen,

        FW_FlugfeldLF,      //Flugfeldlöschfahrzeug
        FW_FlugfeldTreppe,  //Rettungstreppe

        FW_Kran,            //Feuerwehrkran

        FW_TM,              //Teleskopmast
        FW_Turbolöscher,
        FW_ULF,             //Universallöschfahrzeug m. Löscharm

        RD_GW_SAN = 100,    //Gerätewagen Sanitätsdienst (MANV-Zelt)
        RD_LNA,
        RD_OrgL,
        RD_KTW,
        RD_RTW,
        RD_NEF,
        RD_RTH,
        RD_ELW_SAN,
        
        POL_FuStW = 200,    //Streifenwagen
        POL_FüKW,           //Führungskraftwagen (Abteilungsführer)
        POL_leBefKW,        //Leichter Befehlskraftwagen (Zugführer)
        POL_GruKW,          //Gruppenkraftwagen
        POL_GefKW,          //Gefangenenkraftwagen
        POL_WaWe,           //Wasserwerfer
        POL_Hubschrauber,
        POL_MEK,
        POL_SEK,

        THW_GKW = 300,      //Gerätekraftwagen
        THW_MzKW,           //Mehrzweckkraftwagen
        THW_MTW_TZ,         //Mannschaftstransportwagen-Technischer Zug
        THW_BRmGR,          //Berge-Räum-Radlader
        THW_DLE,            //Anhänger Druckluft
        THW_K9,

        WR_GW_Taucher = 400,//Taucher
        WR_Boot,            //Boot

        //Prozedurklassen
        PROC_FW_WATER = 1000,   //FW: Wasser
        PROC_FW_MEN,            //FW: Feuerwehrleute

        PROC_POL_MEN,           //POL: Polizisten
        PROC_POL_PRISONERS,     //POL: Gefangene

        PROC_WR_MEN,            //Wasserrettung-Mann

        NoDemand

    }
    private enum VehicleOrg
    {
        NONE = -1,

        FeuerWehr = 0,
        RettungsDienst,
        Polizei,
        TechnHilfsWerk,
        WasserRettung
    }

    //#########################################################################################

    private class VehicleAlert
    {

        public List<string> ToAlert { get; private set; }
        public List<string> ToModeTractor { get; private set; }

        //#########################################################################################

        public VehicleAlert() { ToAlert = new List<string>(); ToModeTractor = new List<string>(); }

        public void AddVehicle(string id) { if(!ToAlert.Contains(id)) { ToAlert.Add(id); } }
        public void AddTractor(string id) { if(!ToModeTractor.Contains(id)) { ToModeTractor.Add(id); } }

        //#########################################################################################

        public bool Contains(string id) => (ToAlert.Contains(id));

    }

    //#########################################################################################

    private class Patient
    {
        public string ID { get; }
        public string MissionId { get; }
        public string Name { get; }
        public int LivePercentage { get; }
        public int PercentageSpeed { get; }
        public string MissingText { get; }

        public Patient(string id, string missionId, string name, int liveperc, int percspeed, string missingText)
        {
            ID = id;
            MissionId = missionId;
            Name = name;
            LivePercentage = liveperc;
            PercentageSpeed = percspeed;
            MissingText = missingText;
        }
    }

    private class Hospital
    {
        public string ID { get; private set; }
        public string Title { get; private set; }

        public bool IsSuitable { get; private set; }
        public int FreeSlots { get; private set; }

        public Hospital(string id, string title, int free, bool suitable)
        {
            ID = id;
            Title = title;
            IsSuitable = suitable;
            FreeSlots = free;
        }
    }

    //#########################################################################################

    private class Prisoner
    {
        public string ID { get; }
        public string MissionId { get; }
        public string Name { get; }

        public Prisoner(string id, string missionId, string name)
        {
            ID = id;
            MissionId = missionId;
            Name = name;
        }
    }

    private class Cell
    {
        public string ID { get; private set; }
        public string Title { get; private set; }
        public int FreeSlots { get; private set; }

        public Cell(string id, string title, int free)
        {
            ID = id;
            Title = title;
            FreeSlots = free;
        }
    }

    //#########################################################################################

    private class Building
    {

        public string ID { get; }
        public string Title { get; }
        public BuildingType Type { get; }

        public int PersonalCurrent { get; }
        public int PersonalTarget { get; }
        public bool IsPersonalInHire { get; }

        public bool HasPersonalDemand => (PersonalCurrent < PersonalTarget);

        //#########################################################################################

        public Building(string id, string title, BuildingType type, int persCount, int persTarget, bool hire)
        {
            ID = id;
            Title = title;
            Type = type;
            PersonalCurrent = persCount;
            PersonalTarget = persTarget;
            IsPersonalInHire = hire;
        }


    }

    public enum BuildingType
    {
        LEITSTELLE = 7,

        FEUERWACHE = 0,
        FEUERWEHRSCHULE = 1,

        RETTUNGSWACHE = 2,
        SEGWACHE = 12,
        HUBSCHRAUBER_RTH = 5,
        RETTUNGSSCHULE = 3,
        KRANKENHAUS = 4,

        POLIZEIWACHE = 6,
        BEREITSCHAFTSPOLIZEI = 11,
        SPEZIALEINHEIT = 17,
        HUBSCHRAUBER_POLIZEI = 13,
        POLIZEIZELLE = 16,
        POLIZEISCHULE = 8,

        THW = 9,
        THWSCHULE = 10,

        WASSERRETTUNG = 15,

        BEREITSTELLUNGSRAUM = 14
    }

    #endregion

}