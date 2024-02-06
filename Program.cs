
class Coder
{
    static void Main(params string[] args)
    {
        CodeUtils.GetApiKey();
        //CodeUtils.Prepare("/home/greg/code/trail");
        //return;

        var dir = ".";
        if (args.Length > 0)
            dir = args[0];
        if (args.Length > 1 && args[1].StartsWith("-"))
        {
            var option = args[1].TrimStart('-');
            switch (option)
            {
                case "p":
                    CodeUtils.Prepare(dir);
                    break;
                case "c":
                    CodeUtils.Process(dir);
                    break;
                case "m":
                    CodeUtils.Merge(dir);
                    break;
            }
            return;
        }
        CodeUtils.Prepare(dir);
        CodeUtils.Process(dir);
        CodeUtils.Merge(dir);
    }
}
