using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Gloria {
    internal static class AgentCalls {
        static readonly Dictionary<string,string[]> Administrative = new Dictionary<string,string[]> {
            {"codex", new[]{"help","login","logout","mcp","mcp-server","completion","features","apply","debug"}},
            {"claude", new[]{"help","mcp","config","doctor","update","install","migrate-installer","setup-token","plugin","auth"}},
            {"cursor", new[]{"help","--list-models","models","login","logout","status","update","mcp","ls"}},
            {"dsh", new[]{"help","plugin","config","login","logout","update","doctor"}}
        };
        static string Agent(string name) {
            if (name=="cursor-agent") return "cursor";
            return Administrative.ContainsKey(name) ? name : "";
        }
        // Split only at unquoted shell operators. An agent mentioned in a prompt or
        // printed by echo is an argument, not an executable in command position.
        static IEnumerable<List<string>> Commands(string command) {
            var words = new List<string>();
            var token = new StringBuilder();
            char quote = '\0';
            for (int i=0;i<=command.Length;i++) {
                char c = i==command.Length ? '\n' : command[i];
                if (quote!='\0') {
                    if (c==quote) quote='\0';
                    else if ((c=='\\' || c=='\u0060') && i+1<command.Length && command[i+1]==quote) token.Append(command[++i]);
                    else token.Append(c);
                    continue;
                }
                if (c=='"' || c=='\'') {quote=c;continue;}
                bool separator = c=='|' || c==';' || c=='&' || c=='(' || c==')' || c=='\n';
                if (!separator && !Char.IsWhiteSpace(c)) {token.Append(c);continue;}
                if (token.Length>0) {words.Add(token.ToString());token.Clear();}
                if (separator && words.Count>0) {yield return words;words=new List<string>();}
            }
        }
        static string Executable(string token) {
            string name = token.Replace('\\','/').Split('/').Last().ToLowerInvariant();
            return Regex.Replace(name,@"\.(exe|cmd|bat|ps1)$","");
        }
        internal static string Target(string source,string tool,Dictionary<string,object> input) {
            string mcp = tool.Replace("mcp:", "mcp__").Replace(':','_');
            if (mcp.StartsWith("mcp__") || tool=="agent" || tool=="task") {
                string hint = mcp.StartsWith("mcp__") ? mcp.Substring(5) : Events.Field(input,"subagent_type").ToLowerInvariant();
                foreach (string other in Events.Sources)
                    if (other!=source && Regex.IsMatch(hint,@"(^|[^a-z0-9])"+other+@"([^a-z0-9]|$)")) return other;
            }
            if (!(new[]{"bash","powershell","pwsh","shell","exec","exec_command","shell_command"}.Contains(tool))) return "";
            string cmd = Events.Field(input,"command");
            if (cmd=="") cmd=Events.Field(input,"cmd");
            foreach (var words in Commands(cmd)) {
                int i=0;
                while (i<words.Count) {
                    string w=words[i].ToLowerInvariant();
                    if (new[]{"env","nohup","time","command","exec","then","else","do","!"}.Contains(w)
                        || Regex.IsMatch(w,@"^[a-z_][a-z0-9_]*=")) {i++;continue;}
                    if ((w=="timeout" || w=="gtimeout") && i+1<words.Count) {i+=2;continue;}
                    break;
                }
                if (i==words.Count) continue;
                string exe=Executable(words[i]);
                if (exe=="$codex" || exe=="$"+"{codex}") exe="codex";
                if (exe=="cursor") continue; // GUI editor, not cursor-agent
                string agent=Agent(exe);
                if (agent=="" || agent==source) continue;
                string arg=i+1<words.Count ? words[i+1].ToLowerInvariant() : "";
                if (arg=="-v" || arg=="--version" || arg=="-h" || arg=="--help" || Administrative[agent].Contains(arg)) continue;
                return agent;
            }
            return "";
        }
    }
}
