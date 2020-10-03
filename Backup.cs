////NEFs
//int startNEF = m.Missing.MissingNEF;
//while (m.Missing.MissingNEF > 0)
//{

//    //Abbruchbedingung
//    if (canceled.Contains(VehicleClass.RD_NEF))
//    {
//        Print.Info(4, "NEF auf der Verbots-Liste");
//        addCanceled.Add(VehicleClass.RD_NEF);
//        cancelAlert = true;
//        break;
//    }

//    //Fahrzeuge suchen
//    var suitable = Vehicle.Get(m.AvailableVehicles, toAlert.Keys.ToList(), VehicleClass.RD_NEF);
//    string demandString = "[NEF]";

//    //TODO: GRTW schwerverletzt, wenn mehr als fünf NEF

//    if (suitable.Count() == 0)
//    {
//        Print.Info(4, demandString + Print.GetIntentSpacing(demandString, 20) + " // Nicht genug NEF vorhanden.");

//        if (m.Missing.MissingNEF == startNEF)
//        {
//            Print.Info(3, "Es konnte KEIN NEF für diesen Einsatz bereitgestellt werden.");
//            cancelAlert = true;
//        }

//        addCanceled.Add(VehicleClass.RD_NEF);

//        break;
//    }
//    else
//    {
//        var selected = suitable.First();
//        m.Missing.ReduceNEF();
//        if (toAlert.ContainsKey(selected.ID))
//        {
//            toAlert[selected.ID] = new VehicleAlert(selected.ID);
//        }
//        else
//        {
//            toAlert.Add(selected.ID, new VehicleAlert(selected.ID));
//        }

//        Print.Info(4, demandString + Print.GetIntentSpacing(demandString, 20) + " [" + selected.Title + "]");
//    }


//}

////RTWs
//int startRTW = m.Missing.MissingRTW;
//while (m.Missing.MissingRTW > 0 && !cancelAlert)
//{

//    //Abbruchbedingung
//    if (canceled.Contains(VehicleClass.RD_RTW))
//    {
//        Print.Info(4, "RTW auf der Verbots-Liste");
//        addCanceled.Add(VehicleClass.RD_RTW);
//        cancelAlert = true;
//        break;
//    }

//    //Fahrzeuge suchen
//    var suitable = Vehicle.Get(m.AvailableVehicles, toAlert.Keys.ToList(), VehicleClass.RD_RTW);
//    string demandString = "[RTW]";

//    //TODO: GRTW leicht, wenn mehr als zehn RTW

//    if (suitable.Count() == 0)
//    {
//        Print.Info(4, demandString + Print.GetIntentSpacing(demandString, 20) + " // Nicht genug RTW vorhanden.");

//        if (m.Missing.MissingRTW == startRTW)
//        {
//            Print.Info(3, "Es konnte KEIN RTW für diesen Einsatz bereitgestellt werden.");
//            cancelAlert = true;
//        }

//        addCanceled.Add(VehicleClass.RD_RTW);

//        break;
//    }
//    else
//    {
//        var selected = suitable.First();
//        m.Missing.ReduceRTW();
//        if (toAlert.ContainsKey(selected.ID))
//        {
//            toAlert[selected.ID] = new VehicleAlert(selected.ID);
//        }
//        else
//        {
//            toAlert.Add(selected.ID, new VehicleAlert(selected.ID));
//        }

//        Print.Info(4, demandString + Print.GetIntentSpacing(demandString, 20) + " [" + selected.Title + "]");
//    }
//}

////KTWs
//int startKTW = m.Missing.MissingKTW;
//while (m.Missing.MissingKTW > 0 && !cancelAlert)
//{

//    //Abbruchbedingung
//    if (canceled.Contains(VehicleClass.RD_KTW))
//    {
//        Print.Info(4, "KTW auf der Verbots-Liste");
//        addCanceled.Add(VehicleClass.RD_KTW);
//        cancelAlert = true;
//        break;
//    }

//    //Fahrzeuge suchen
//    var suitable = Vehicle.Get(m.AvailableVehicles, toAlert.Keys.ToList(), VehicleClass.RD_KTW);

//    string demandString = "[KTW]";

//    if (suitable.Count() == 0)
//    {
//        Print.Info(4, demandString + Print.GetIntentSpacing(demandString, 20) + " // Kein Krankentransport-Fahrzeug vorhanden.");

//        if (m.Missing.MissingKTW == startKTW)
//        {
//            Print.Info(3, "Es konnte KEIN KTW für diesen Einsatz bereitgestellt werden.");
//            cancelAlert = true;
//        }

//        addCanceled.Add(VehicleClass.RD_KTW);

//        break;
//    }
//    else
//    {
//        var selected = suitable.First();
//        m.Missing.ReduceKTW();
//        if (toAlert.ContainsKey(selected.ID))
//        {
//            toAlert[selected.ID] = new VehicleAlert(selected.ID);
//        }
//        else
//        {
//            toAlert.Add(selected.ID, new VehicleAlert(selected.ID));
//        }

//        Print.Info(4, demandString + Print.GetIntentSpacing(demandString, 20) + " [" + selected.Title + "]");
//    }


//}