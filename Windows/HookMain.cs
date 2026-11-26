using System;
using System.IO;
using System.Text;

namespace Gloria {
    static class HookProgram {
        static int Main(string[] args) {
            if ((args.Length == 2 || args.Length == 3) && (args[0] == "--gloria-hook" || args[0] == "--hook")) {
                return Events.Forward(args[1], args.Length == 3 ? args[2] : null);
            }
            if (args.Length >= 1 && args[0] == "--ctl") {
                string payload = args.Length >= 2 ? args[1] : "";
                if (payload == "-" || String.IsNullOrEmpty(payload)) {
                    using (var sr = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8, true)) {
                        payload = sr.ReadToEnd();
                    }
                }
                return Events.Control(payload);
            }
            return 2;
        }
    }
}
