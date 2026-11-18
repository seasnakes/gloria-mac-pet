namespace Gloria {
    static class HookMain {
        static int Main(string[] args) {
            return args.Length==2 && args[0]=="--gloria-hook"?Events.Forward(args[1]):2;
        }
    }
}
