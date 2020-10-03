using System.Threading.Tasks;

public class Program
{

    public const bool debug_readonly = false;

    public static async Task Main(string[] args)
    {

        if (args.Length == 0)
        {
            var bot = new Bot();
            await bot.StartBot();
        }
        else
        {
            if(args[0] == "/reset")
            {
                var bot = new Bot();
                await bot.Reset();
            }
        }


    }

}
