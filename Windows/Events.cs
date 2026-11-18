using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace Gloria {
    // A message-only HWND is local to the interactive desktop. No listener port, files or queues.
    internal static class Events {
        internal const int WM_COPYDATA = 0x4a, Tag = 0x474c52;
        internal static string Address { get { string profile=Environment.GetEnvironmentVariable("GLORIA_DATA_DIR")??"";
            using(var sha=SHA256.Create()) return "GLORIA.Windows." + WindowsIdentity.GetCurrent().User.Value+"."+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(profile)),0,8); } }
        [StructLayout(LayoutKind.Sequential)] internal struct Packet { public IntPtr tag; public int size; public IntPtr data; }
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr from, ref Packet packet, uint flags, uint timeout, out IntPtr result);
        internal static readonly string[] Sources = { "claude", "codex", "cursor", "dsh" };
        internal static string Field(Dictionary<string,object> d, string k) {
            object v; return d != null && d.TryGetValue(k,out v) ? v as string ?? "" : "";
        }
        internal static string[] Normalize(string source, Dictionary<string,object> e) {
            object v; var input = e.TryGetValue("tool_input", out v) ? v as Dictionary<string,object> : null;
            string ev = Field(e,"hook_event_name").ToLowerInvariant(), tool = Field(e,"tool_name").ToLowerInvariant();
            string anim, line;
            switch(ev) {
            case "sessionstart": anim="wave"; line="来啦"; break;
            case "userpromptsubmit": case "beforesubmitprompt": anim="work"; line="收到，开工"; break;
            case "permissionrequest": anim="waiting"; line="等你批准"; break;
            case "notification":
                if(Field(e,"notification_type")!="permission_prompt") return null;
                anim="waiting"; line="等你批准"; break;
            case "pretooluse": case "posttooluse":
                string action=Field(input,"action").ToLowerInvariant();
                string code=tool.Contains("cua_repl") ? Field(input,"code") : "";
                anim="work"; line="正在使用工具";
                if(tool.Contains("computer") || tool.Contains("cua_repl")) {
                    if(action.Contains("screenshot") || code.Contains(".getScreenshot(")){ anim="snap"; line="截个图"; }
                    else if(action=="type" || action=="key" || code.Contains(".typeText(") || code.Contains(".pressKey(")){ anim="type"; line="敲键盘"; }
                    else if(action.Contains("drag") || code.Contains(".drag(")){ anim="drag"; line="拖动鼠标"; }
                    else if(action.Contains("scroll") || code.Contains(".scroll(")){ anim="scroll"; line="滚动页面"; }
                    else if(action.Contains("click") || code.Contains(".click(")){ anim="tap"; line="点一下"; }
                } else if(tool=="request_user_input"){anim="waiting";line="想问你个问题";}
                else if(tool=="bash" || tool=="powershell" || tool=="pwsh" || tool.Contains("shell") || tool=="exec_command") line="在跑命令";
                else if(tool.Contains("read")) line="在看文件";
                else if(tool.Contains("edit") || tool.Contains("write") || tool=="apply_patch") line="在改文件";
                break;
            case "posttoolusefailure": case "stopfailure": anim="failed"; line="这一步没成功"; break;
            case "subagentstart": anim="work"; line="叫了个帮手"; break;
            case "subagentstop": anim="work"; line="帮手回来啦"; break;
            case "precompact": anim="review"; line="整理上下文"; break;
            case "stop": case "sessionend":
                anim=Field(e,"status")=="error"?"failed":"wave"; line=anim=="failed"?"这一步没成功":"做完啦"; break;
            default: return null;
            }
            string session=Field(e,"session_id");
            if(session=="") session=Field(e,"conversation_id");
            bool ended=ev=="stop" || ev=="sessionend" || ev=="stopfailure";
            var phase = "state";
            if (ev == "pretooluse") phase = "begin";
            if (ev == "posttooluse" || ev == "posttoolusefailure") {
                object error, response;
                bool failed = ev == "posttoolusefailure" || (e.TryGetValue("error",out error) && error != null && error.ToString() != "" && !Object.Equals(error,false));
                if (e.TryGetValue("tool_response",out response) && response is Dictionary<string,object>) {
                    var result = (Dictionary<string,object>)response;
                    object value;
                    failed |= result.TryGetValue("is_error",out value) && Object.Equals(value,true);
                    failed |= result.TryGetValue("exit_code",out value) && value is int && (int)value != 0;
                }
                phase = failed ? "fail" : "finish";
            }
            string id = Field(e,"tool_use_id");
            if (id=="") id=Field(e,"call_id");
            if (id=="") id=Field(e,"tool_call_id");
            string target = phase=="state" ? "" : AgentCalls.Target(source,tool,input);
            // Some clients omit both the ID and the input from the completion event.
            // Keep a tool fingerprint to match a unique outstanding call in that session.
            string toolKey=tool==""?"":Hash(tool);
            bool inputMissing=input==null || input.Count==0;
            return new[]{source,Hash(session),anim,line,ended?"end":"open",phase,id==""?"":Hash(id),target,toolKey,inputMissing?"empty":"input"};
        }
        static string Hash(string value) {
            using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)),0,8);
        }
        internal static void Send(string[] fields) {
            IntPtr hwnd=FindWindowEx(new IntPtr(-3),IntPtr.Zero,null,Address);
            if(hwnd==IntPtr.Zero) return;
            string s=String.Join("\t",fields);
            Packet p=new Packet{tag=(IntPtr)Tag,size=s.Length*2,data=Marshal.StringToHGlobalUni(s)};
            try {IntPtr result; SendMessageTimeout(hwnd,WM_COPYDATA,IntPtr.Zero,ref p,2,400,out result);}
            finally {Marshal.FreeHGlobal(p.data);}
        }
        internal static int Forward(string source) {
            string reply = "{\"permission\":\"allow\"}";
            try {
                if(Array.IndexOf(Sources,source)<0) return 2;
                using(var reader=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8,true)) {
                    var buf=new char[8192]; var text=new StringBuilder(); int n;
                    while((n=reader.Read(buf,0,buf.Length))>0) {
                        if(text.Length+n>2*1024*1024) {if(source=="cursor") Console.WriteLine(reply);return 0;}
                        text.Append(buf,0,n);
                    }
                    var e=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(text.ToString());
                    if (Field(e,"hook_event_name")!="preToolUse") reply="{}";
                    var fields=Normalize(source,e);
                    if(fields!=null) Send(fields);
                }
            } catch { /* An observer must not interrupt a tool call. */ }
            if(source=="cursor") Console.WriteLine(reply);
            return 0;
        }
    }
}
