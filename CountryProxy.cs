using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BrowserCountryProxy {
    sealed class Adapter {
        public string Name, Address, Description;
        public int Index;
        public uint Metric;
    }
    sealed class SavedValue {
        public string Path, Name, Kind;
        public bool Exists;
        public object Value;
    }
    sealed class MailBackup {
        public string Path;
        public bool Existed;
        public string OriginalProxyLine;
    }
    sealed class Backup {
        public List<SavedValue> Registry = new List<SavedValue>();
        public List<MailBackup> Mail = new List<MailBackup>();
    }
    sealed class State {
        public int ProxyPort, PacPort;
        public bool Configured;
    }
    static class Native {
        [DllImport("iphlpapi.dll")] public static extern int GetIpForwardTable(IntPtr buffer, ref int size, bool order);
        [DllImport("wininet.dll", EntryPoint="InternetSetOptionW", ExactSpelling=true, SetLastError=true)]
        static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);
        [DllImport("winhttp.dll", SetLastError=true)] static extern bool WinHttpGetIEProxyConfigForCurrentUser(out IEConfig value);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr handle);
        [StructLayout(LayoutKind.Sequential)] struct IEConfig { public int Detect; public IntPtr Pac, Proxy, Bypass; }
        [StructLayout(LayoutKind.Explicit, Size=8)] struct OptionValue { [FieldOffset(0)] public uint Number; [FieldOffset(0)] public IntPtr Text; }
        [StructLayout(LayoutKind.Sequential)] struct Option { public uint Id; public OptionValue Value; }
        [StructLayout(LayoutKind.Sequential)] struct OptionList { public uint Size; public IntPtr Connection; public uint Count, Error; public IntPtr Options; }
        public static List<uint[]> Routes() {
            int size = 0; GetIpForwardTable(IntPtr.Zero, ref size, false);
            IntPtr memory = Marshal.AllocHGlobal(size);
            try {
                int result = GetIpForwardTable(memory, ref size, false);
                if (result != 0) throw new InvalidOperationException("Не удалось прочитать маршруты: " + result);
                int count = Marshal.ReadInt32(memory);
                var rows = new List<uint[]>();
                for (int i=0; i<count; i++) {
                    uint[] row = new uint[14];
                    for (int j=0; j<14; j++) row[j] = unchecked((uint)Marshal.ReadInt32(memory, 4+i*56+j*4));
                    rows.Add(row);
                }
                return rows;
            } finally { Marshal.FreeHGlobal(memory); }
        }
        public static string CurrentPac() {
            IEConfig config;
            if (!WinHttpGetIEProxyConfigForCurrentUser(out config)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { return config.Pac == IntPtr.Zero ? null : Marshal.PtrToStringUni(config.Pac); }
            finally { if (config.Pac!=IntPtr.Zero) GlobalFree(config.Pac); if(config.Proxy!=IntPtr.Zero) GlobalFree(config.Proxy); if(config.Bypass!=IntPtr.Zero) GlobalFree(config.Bypass); }
        }
        public static void Refresh() { InternetSetOption(IntPtr.Zero, 95, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); }
        public static void SetPac(string url) {
            int optionSize = Marshal.SizeOf(typeof(Option));
            IntPtr options = Marshal.AllocHGlobal(optionSize*2), text = Marshal.StringToHGlobalUni(url), list = IntPtr.Zero;
            try {
                Marshal.StructureToPtr(new Option { Id=1, Value=new OptionValue { Number=5 } }, options, false);
                Marshal.StructureToPtr(new Option { Id=4, Value=new OptionValue { Text=text } }, IntPtr.Add(options,optionSize), false);
                var data = new OptionList { Size=(uint)Marshal.SizeOf(typeof(OptionList)), Count=2, Options=options };
                list=Marshal.AllocHGlobal((int)data.Size); Marshal.StructureToPtr(data,list,false);
                if(!InternetSetOption(IntPtr.Zero,75,list,(int)data.Size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                int optionError=Marshal.ReadInt32(list,20);
                if(optionError!=0) throw new InvalidOperationException("Не удалось применить параметр прокси: "+optionError);
                Refresh();
                if(CurrentPac()!=url) {
                    using(var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings")) {
                        key.SetValue("AutoConfigURL",url,RegistryValueKind.String);
                        key.SetValue("ProxyEnable",0,RegistryValueKind.DWord);
                    }
                    Refresh();
                }
                if(CurrentPac()!=url) throw new InvalidOperationException("Windows не сохранила автоматическую настройку прокси. Прежние настройки будут восстановлены.");
            } finally { if(list!=IntPtr.Zero) Marshal.FreeHGlobal(list); Marshal.FreeHGlobal(options); Marshal.FreeHGlobal(text); }
        }
    }
    static class Program {
        const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "BrowserCountryProxy";
        const string MailStart = "// BEGIN_COUNTRY_PROXY_PORTABLE";
        const string MailEnd = "// END_COUNTRY_PROXY_PORTABLE";
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength=10485760 };
        static string Root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        static bool SkipMail;
        static string Exe { get { return Path.Combine(Root,"CountryProxy.exe"); } }
        static string InstalledRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BrowserCountryProxy"); } }
        static string Namespace {
            get { using(var sha=SHA256.Create()) return "Local\\BrowserCountryProxy-"+BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(Root.ToLowerInvariant()))).Replace("-","").Substring(0,16); }
        }
        static string PacUrl(State state) { return "http://127.0.0.1:"+state.PacPort+"/proxy.pac"; }
        static T Read<T>(string name) { return Json.Deserialize<T>(File.ReadAllText(Path.Combine(Root,name),Utf8)); }
        static void Save(string name, object value) { File.WriteAllText(Path.Combine(Root,name),Json.Serialize(value),Utf8); }
        static void Log(string value) { try { File.AppendAllText(Path.Combine(Root,"manager.log"),DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ")+value+Environment.NewLine,Utf8); } catch { } }
        static string Arg(string[] args,string key) { int n=Array.IndexOf(args,key); return n>=0 && n+1<args.Length ? args[n+1] : null; }
        static Dictionary<string,object> D(params object[] values) {
            var result=new Dictionary<string,object>(); for(int i=0;i<values.Length;i+=2) result[(string)values[i]]=values[i+1]; return result;
        }
        static Adapter SelectAdapter() {
            var routes=Native.Routes(); var choices=new List<Adapter>();
            foreach(var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                if(nic.OperationalStatus!=OperationalStatus.Up) continue;
                if(nic.NetworkInterfaceType!=NetworkInterfaceType.Wireless80211 && nic.NetworkInterfaceType!=NetworkInterfaceType.Ethernet) continue;
                string description=(nic.Name+" "+nic.Description).ToLowerInvariant();
                string[] virtualWords={"tap-windows","tap-win32","wintun","wireguard","amnezia","vpn","virtual","vethernet","hyper-v","vmware","loopback","wi-fi direct","wifi direct","bluetooth","tailscale","zerotier","tunnel"};
                if(virtualWords.Any(w=>description.Contains(w))) continue;
                var properties=nic.GetIPProperties();
                if(!properties.GatewayAddresses.Any(g=>g.Address.AddressFamily==AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                var ip=properties.UnicastAddresses.FirstOrDefault(a=>a.Address.AddressFamily==AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.") && !IPAddress.IsLoopback(a.Address));
                if(ip==null || properties.GetIPv4Properties()==null) continue;
                int index=properties.GetIPv4Properties().Index;
                var defaults=routes.Where(r=>r[0]==0 && r[1]==0 && r[4]==(uint)index).ToList();
                uint metric=defaults.Count==0 ? uint.MaxValue : defaults.Min(r=>r[9]);
                choices.Add(new Adapter {Name=nic.Name,Address=ip.Address.ToString(),Description=nic.Description,Index=index,Metric=metric});
            }
            return choices.OrderBy(a=>a.Metric).ThenBy(a=>a.Index).FirstOrDefault();
        }
        static object Config(Adapter adapter,State state) {
            return D("log",D("level","warn","timestamp",true,"output",Path.Combine(Root,"proxy.log")),
                "dns",D("servers",new[]{D("type","https","tag","dns","server","1.1.1.1","bind_interface",adapter.Name,"inet4_bind_address",adapter.Address)},"final","dns","strategy","ipv4_only"),
                "inbounds",new[]{D("type","mixed","tag","apps","listen","127.0.0.1","listen_port",state.ProxyPort)},
                "outbounds",new[]{D("type","direct","tag","ru-direct","bind_interface",adapter.Name,"inet4_bind_address",adapter.Address,"connect_timeout","12s"),D("type","direct","tag","existing-connection","connect_timeout","12s")},
                "route",D("rules",new[]{D("action","resolve","strategy","ipv4_only"),D("ip_is_private",true,"action","route","outbound","ru-direct"),D("rule_set","geoip-ru","action","route","outbound","ru-direct")},
                    "rule_set",new[]{D("type","local","tag","geoip-ru","format","binary","path",Path.Combine(Root,"geoip-ru.srs"))},"final","existing-connection","default_domain_resolver","dns"));
        }
        static int FreePort(int preferred) {
            try { var listener=new TcpListener(IPAddress.Loopback,preferred); listener.Start(); listener.Stop(); return preferred; }
            catch(SocketException) { var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); int port=((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
        }
        static bool Alive(State state) {
            try {
                var request=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+state.PacPort+"/health"); request.Proxy=null; request.Timeout=700;
                using(var response=request.GetResponse()) using(var reader=new StreamReader(response.GetResponseStream())) if(reader.ReadToEnd()!="BrowserCountryProxy") return false;
                using(var client=new TcpClient()) {
                    var task=client.ConnectAsync(IPAddress.Loopback,state.ProxyPort);
                    if(!task.Wait(700) || !client.Connected) return false;
                    client.ReceiveTimeout=700;client.SendTimeout=700;
                    var stream=client.GetStream();stream.Write(new byte[]{5,1,0},0,3);
                    byte[] reply=ReadBytes(stream,2);return reply[0]==5 && reply[1]==0;
                }
            } catch { return false; }
        }
        static void StopWorker() {
            try { using(var stop=EventWaitHandle.OpenExisting(Namespace+"-stop")) stop.Set(); } catch(WaitHandleCannotBeOpenedException) { return; }
            for(int i=0;i<100;i++) {
                try { using(var mutex=Mutex.OpenExisting(Namespace+"-running")) {} } catch(WaitHandleCannotBeOpenedException) { return; }
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("Прокси не остановился. Настройки Windows уже можно восстановить; повторите отключение через несколько секунд.");
        }
        static Process Launch(string args) {
            return Process.Start(new ProcessStartInfo(Exe,args) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Root});
        }
        static Process StartEngine() {
            return Process.Start(new ProcessStartInfo(Path.Combine(Root,"sing-box.exe"),"run -c \""+Path.Combine(Root,"config.json")+"\"") {
                UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Root,
                RedirectStandardOutput=true,RedirectStandardError=true});
        }
        static void EndEngine(Process process) {
            if(process==null) return;
            try { if(!process.HasExited) { process.Kill(); process.WaitForExit(5000); } } finally { process.Dispose(); }
        }
        static void Serve(State state) {
            bool created;
            using(var mutex=new Mutex(false,Namespace+"-running",out created)) {
                if(!created) return;
                using(var stop=new EventWaitHandle(false,EventResetMode.ManualReset,Namespace+"-stop")) {
                    var listener=new TcpListener(IPAddress.Loopback,state.PacPort); Process engine=null; string last=null; long lastCheck=0;
                    try {
                        listener.Start();
                        while(!stop.WaitOne(100)) {
                            if(listener.Pending()) {
                                var client=listener.AcceptTcpClient();
                                ThreadPool.QueueUserWorkItem(_=>AnswerPac(client,state));
                            }
                            if(DateTime.UtcNow.Ticks-lastCheck<TimeSpan.TicksPerSecond*2) continue;
                            lastCheck=DateTime.UtcNow.Ticks;
                            var adapter=SelectAdapter();
                            string current=adapter==null ? null : adapter.Name+"|"+adapter.Address;
                            if(adapter!=null && (current!=last || engine==null || engine.HasExited)) {
                                EndEngine(engine); engine=null;
                                Save("config.json",Config(adapter,state));
                                engine=StartEngine();
                                engine.OutputDataReceived+=(s,e)=>{};
                                engine.ErrorDataReceived+=(s,e)=>{if(e.Data!=null) Log("engine: "+e.Data);};
                                engine.BeginOutputReadLine(); engine.BeginErrorReadLine();
                                last=current; Log("Прокси: "+adapter.Name+" "+adapter.Address);
                            }
                            if(engine!=null && engine.HasExited) stop.WaitOne(2000);
                            State currentState=null;
                            try {currentState=Read<State>("state.json");} catch(IOException) {} catch(ArgumentException) {}
                            if(currentState!=null && currentState.Configured && engine!=null && !engine.HasExited && Native.CurrentPac()!=PacUrl(state)) {
                                Native.SetPac(PacUrl(state));Log("Настройка прокси обновлена");
                            }
                        }
                    } finally { listener.Stop(); EndEngine(engine); Log("Прокси остановлен"); }
                }
            }
        }
        static void AnswerPac(TcpClient client,State state) {
            using(client) {
                try {
                    client.ReceiveTimeout=3000; client.SendTimeout=3000;
                    using(var stream=client.GetStream()) {
                        byte[] requestBytes=new byte[4096]; int size=stream.Read(requestBytes,0,requestBytes.Length);
                        string request=Encoding.ASCII.GetString(requestBytes,0,size);
                        bool health=request.StartsWith("GET /health "), pac=request.StartsWith("GET /proxy.pac ");
                        string content=health ? "BrowserCountryProxy" : pac ? "function FindProxyForURL(url, host) { if (host === 'localhost' || host === '127.0.0.1' || host === '::1' || isPlainHostName(host)) return 'DIRECT'; return 'SOCKS5 127.0.0.1:"+state.ProxyPort+"; DIRECT'; }\n" : "Not found";
                        byte[] body=Encoding.ASCII.GetBytes(content), headers=Encoding.ASCII.GetBytes("HTTP/1.1 "+(pac||health ? "200 OK" : "404 Not Found")+"\r\nContent-Type: "+(pac ? "application/x-ns-proxy-autoconfig" : "text/plain")+"\r\nContent-Length: "+body.Length+"\r\nCache-Control: no-cache, max-age=0\r\nConnection: close\r\n\r\n");
                        stream.Write(headers,0,headers.Length); stream.Write(body,0,body.Length);
                    }
                } catch { }
            }
        }
        static SavedValue GetValue(string path,string name) {
            using(var key=Registry.CurrentUser.OpenSubKey(path)) {
                bool exists=key!=null && key.GetValueNames().Contains(name);
                if(!exists) return new SavedValue {Path=path,Name=name};
                object value=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames); var kind=key.GetValueKind(name);
                return new SavedValue {Path=path,Name=name,Exists=true,Kind=kind.ToString(),Value=kind==RegistryValueKind.Binary ? Convert.ToBase64String((byte[])value) : value};
            }
        }
        static void Restore(SavedValue entry) {
            using(var key=Registry.CurrentUser.CreateSubKey(entry.Path)) {
                if(!entry.Exists) { key.DeleteValue(entry.Name,false); return; }
                var kind=(RegistryValueKind)Enum.Parse(typeof(RegistryValueKind),entry.Kind); object value=entry.Value;
                if(kind==RegistryValueKind.Binary) value=Convert.FromBase64String((string)value);
                if(kind==RegistryValueKind.DWord) value=Convert.ToInt32(value);
                if(kind==RegistryValueKind.QWord) value=Convert.ToInt64(value);
                if(kind==RegistryValueKind.MultiString) value=((System.Collections.ArrayList)value).Cast<string>().ToArray();
                key.SetValue(entry.Name,value,kind);
            }
        }
        static List<string> MailProfiles() {
            string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Thunderbird"), ini=Path.Combine(folder,"profiles.ini");
            var profiles=new List<string>(); if(!File.Exists(ini)) return profiles;
            string section=""; var entries=new Dictionary<string,string>();
            Action flush=()=>{
                string path;
                if(section.StartsWith("Profile",StringComparison.OrdinalIgnoreCase) && entries.TryGetValue("Path",out path)) {
                    string relative; entries.TryGetValue("IsRelative",out relative);
                    path=path.Replace('/',Path.DirectorySeparatorChar);
                    string full=Path.GetFullPath(relative=="1" ? Path.Combine(folder,path) : path);
                    if(Directory.Exists(full) && File.Exists(Path.Combine(full,"prefs.js"))) profiles.Add(full);
                }
            };
            foreach(string raw in File.ReadAllLines(ini)) {
                string line=raw.Trim();
                if(line.StartsWith("[") && line.EndsWith("]")) { flush();section=line.Substring(1,line.Length-2);entries.Clear(); }
                else { int n=line.IndexOf('='); if(n>0) entries[line.Substring(0,n).Trim()]=line.Substring(n+1).Trim(); }
            }
            flush(); return profiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        static void AddMail(MailBackup record) {
            string text=File.Exists(record.Path) ? File.ReadAllText(record.Path,Utf8) : "";
            if(!text.Contains(MailStart)) File.WriteAllText(record.Path,text+"\n"+MailStart+"\nuser_pref(\"network.proxy.type\", 5);\n"+MailEnd+"\n",Utf8);
        }
        static void RemoveMail(MailBackup record) {
            if(!File.Exists(record.Path)) return;
            string text=File.ReadAllText(record.Path,Utf8);
            text=System.Text.RegularExpressions.Regex.Replace(text,"\\n?"+MailStart+"\\n.*?"+MailEnd+"\\n?","",System.Text.RegularExpressions.RegexOptions.Singleline);
            if(!record.Existed && string.IsNullOrWhiteSpace(text)) File.Delete(record.Path); else File.WriteAllText(record.Path,text,Utf8);
            string prefs=Path.Combine(Path.GetDirectoryName(record.Path),"prefs.js");
            if(File.Exists(prefs)) {
                string current=File.ReadAllText(prefs,Utf8);
                current=System.Text.RegularExpressions.Regex.Replace(current,"^user_pref\\(\"network\\.proxy\\.type\", 5\\);\\r?\\n?",record.OriginalProxyLine==null ? "" : record.OriginalProxyLine+"\n",System.Text.RegularExpressions.RegexOptions.Multiline);
                File.WriteAllText(prefs,current,Utf8);
            }
        }
        static Backup SaveBackup(bool fresh) {
            if(!fresh && File.Exists(Path.Combine(Root,"settings-before.json"))) return Read<Backup>("settings-before.json");
            var backup=new Backup();
            foreach(string name in new[]{"AutoConfigURL","ProxyEnable","ProxyServer","ProxyOverride","AutoDetect"}) backup.Registry.Add(GetValue(SettingsKey,name));
            foreach(string name in new[]{"DefaultConnectionSettings","SavedLegacySettings"}) backup.Registry.Add(GetValue(SettingsKey+@"\Connections",name));
            backup.Registry.Add(GetValue(RunKey,RunName));
            foreach(string profile in SkipMail ? new List<string>() : MailProfiles()) {
                string prefs=File.ReadAllText(Path.Combine(profile,"prefs.js"),Utf8);
                var match=System.Text.RegularExpressions.Regex.Match(prefs,"^user_pref\\(\"network\\.proxy\\.type\",[^\\r\\n]+",System.Text.RegularExpressions.RegexOptions.Multiline);
                backup.Mail.Add(new MailBackup {Path=Path.Combine(profile,"user.js"),Existed=File.Exists(Path.Combine(profile,"user.js")),OriginalProxyLine=match.Success ? match.Value : null});
            }
            Save("settings-before.json",backup); return backup;
        }
        static void Enable() {
            var adapter=SelectAdapter(); if(adapter==null) throw new InvalidOperationException("Не найден подключённый Wi-Fi или Ethernet с IPv4 и шлюзом. Настройки не изменены.");
            State state;
            if(File.Exists(Path.Combine(Root,"state.json"))) state=Read<State>("state.json");
            else { state=new State {ProxyPort=FreePort(10808),PacPort=FreePort(18080)}; Save("state.json",state); }
            if(!Alive(state)) {
                using(var worker=Launch("--run")) {
                    bool ready=false;
                    for(int i=0;i<70;i++) { if(worker.HasExited) break; if(Alive(state)) {ready=true;break;} Thread.Sleep(200); }
                    if(!ready) { try { StopWorker(); } catch {} throw new InvalidOperationException("Прокси не запустился. Настройки Windows не изменены. Подробности: "+Path.Combine(Root,"manager.log")); }
                }
            }
            var backup=SaveBackup(!state.Configured && Native.CurrentPac()!=PacUrl(state));
            try {
                foreach(var mail in backup.Mail) AddMail(mail);
                Native.SetPac(PacUrl(state));
                using(var key=Registry.CurrentUser.CreateSubKey(RunKey)) key.SetValue(RunName,"\""+Exe+"\" --run",RegistryValueKind.String);
                state.Configured=true; Save("state.json",state);
                Log("Разделение включено");
            } catch { Disable(); throw; }
        }
        static void Disable() {
            if(File.Exists(Path.Combine(Root,"state.json"))) { var state=Read<State>("state.json");state.Configured=false;Save("state.json",state); }
            Exception stopError=null;
            try {StopWorker();} catch(Exception ex) {stopError=ex;}
            if(File.Exists(Path.Combine(Root,"settings-before.json"))) {
                var backup=Read<Backup>("settings-before.json");
                foreach(var entry in backup.Registry) Restore(entry);
                Native.Refresh();
                foreach(var mail in backup.Mail) RemoveMail(mail);
            }
            Log("Прежние настройки восстановлены");
            if(stopError!=null) throw stopError;
        }
        static void Install() {
            if(!Environment.Is64BitOperatingSystem) throw new InvalidOperationException("Этот комплект предназначен для 64-битной Windows 10/11.");
            if(SelectAdapter()==null) throw new InvalidOperationException("Сначала подключите Wi-Fi или Ethernet.");
            if(!string.Equals(Root,InstalledRoot,StringComparison.OrdinalIgnoreCase)) {
                Directory.CreateDirectory(InstalledRoot);
                string[] payload={"CountryProxy.exe","sing-box.exe","geoip-ru.srs","sing-box-LICENSE.txt","THIRD_PARTY.txt"};
                foreach(string name in payload) {
                    string source=Path.Combine(Root,name); if(!File.Exists(source)) throw new FileNotFoundException("В архиве отсутствует "+name);
                    string target=Path.Combine(InstalledRoot,name);
                    if(File.Exists(target)) {
                        using(var sha=SHA256.Create()) if(sha.ComputeHash(File.ReadAllBytes(source)).SequenceEqual(sha.ComputeHash(File.ReadAllBytes(target)))) continue;
                    }
                    File.Copy(source,target,true);
                }
                string resultPath=Path.Combine(InstalledRoot,"install-result.json");
                using(var child=Process.Start(new ProcessStartInfo(Path.Combine(InstalledRoot,"CountryProxy.exe"),"--enable --quiet --result \""+resultPath+"\"") {UseShellExecute=false,CreateNoWindow=true})) {
                    child.WaitForExit();
                    if(child.ExitCode!=0) {
                        string reason="Смотрите manager.log.";
                        if(File.Exists(resultPath)) {var details=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(resultPath,Utf8));if(details.ContainsKey("error")) reason=(string)details["error"];}
                        throw new InvalidOperationException("Установка не завершена. "+reason);
                    }
                }
            } else Enable();
        }
        static bool TestTls(string host,int port,State state) {
            using(var client=new TcpClient()) {
                if(!client.ConnectAsync(IPAddress.Loopback,state.ProxyPort).Wait(12000)) throw new TimeoutException();
                client.ReceiveTimeout=15000;client.SendTimeout=15000;
                var stream=client.GetStream(); stream.Write(new byte[]{5,1,0},0,3);
                byte[] greeting=ReadBytes(stream,2); if(greeting[1]!=0) throw new IOException("SOCKS authentication failed");
                byte[] name=Encoding.ASCII.GetBytes(host), request=new byte[7+name.Length];
                request[0]=5;request[1]=1;request[3]=3;request[4]=(byte)name.Length;Array.Copy(name,0,request,5,name.Length);request[5+name.Length]=(byte)(port>>8);request[6+name.Length]=(byte)port;
                stream.Write(request,0,request.Length);byte[] header=ReadBytes(stream,4);if(header[1]!=0)throw new IOException("SOCKS response "+header[1]);
                int n=header[3]==1 ? 4 : header[3]==4 ? 16 : ReadBytes(stream,1)[0];ReadBytes(stream,n+2);
                using(var tls=new SslStream(stream,false)) { tls.ReadTimeout=15000;tls.WriteTimeout=15000;tls.AuthenticateAsClient(host,null,System.Security.Authentication.SslProtocols.Tls12,false); return tls.IsAuthenticated && tls.IsEncrypted; }
            }
        }
        static byte[] ReadBytes(Stream stream,int count) { byte[] data=new byte[count]; int offset=0;while(offset<count) {int n=stream.Read(data,offset,count-offset);if(n==0)throw new EndOfStreamException();offset+=n;}return data; }
        static string RouteFingerprint() { return string.Join(";",Native.Routes().Select(r=>string.Join(",",r.Where((v,i)=>i!=7).Select(v=>v.ToString()).ToArray())).OrderBy(r=>r).ToArray()); }
        static object SelfTest() {
            var adapter=SelectAdapter();if(adapter==null)throw new InvalidOperationException("No physical adapter");
            var state=new State {ProxyPort=FreePort(10818),PacPort=FreePort(18090)}; Save("state.json",state);
            Save("config.json",Config(adapter,state));
            using(var check=Process.Start(new ProcessStartInfo(Path.Combine(Root,"sing-box.exe"),"check -c \""+Path.Combine(Root,"config.json")+"\""){UseShellExecute=false,CreateNoWindow=true})) {check.WaitForExit();if(check.ExitCode!=0)throw new InvalidOperationException("Configuration check failed");}
            string before=RouteFingerprint(), pacBefore=Native.CurrentPac();bool github=false,beget=false,smtp=false;
            try {
                using(var worker=Launch("--run")) {
                    for(int i=0;i<70 && !Alive(state);i++) {if(worker.HasExited)throw new InvalidOperationException("Worker exited");Thread.Sleep(200);}
                    if(!Alive(state)) throw new InvalidOperationException("Worker not ready");
                    github=TestTls("github.com",443,state);beget=TestTls("imap.beget.com",993,state);smtp=TestTls("smtp.beget.com",465,state);
                }
            } finally {StopWorker();}
            return D("adapter",adapter.Name,"address",adapter.Address,"github_tls",github,"beget_imap_tls",beget,"beget_smtp_tls",smtp,"routes_unchanged",before==RouteFingerprint(),"windows_proxy_unchanged",pacBefore==Native.CurrentPac());
        }
        [STAThread] static int Main(string[] args) {
            bool quiet=args.Contains("--quiet");string result=Arg(args,"--result");
            SkipMail=args.Contains("--skip-mail");
            string work=Arg(args,"--work-dir");if(work!=null)Root=Path.GetFullPath(work);
            try {
                Directory.CreateDirectory(Root);object report=null;string message="";
                if(args.Contains("--self-test")) report=SelfTest();
                else if(args.Contains("--run")) Serve(Read<State>("state.json"));
                else if(args.Contains("--install")) {Install();message="Установлено. VPN подключайте как обычно.\nПерезапустите Thunderbird, если он был открыт.\nДля отключения запустите DISABLE.bat.";}
                else if(args.Contains("--enable") || args.Contains("--disable") || args.Contains("--status")) {
                    if(!string.Equals(Root,InstalledRoot,StringComparison.OrdinalIgnoreCase) && work==null) {
                        if(!File.Exists(Path.Combine(InstalledRoot,"CountryProxy.exe"))) throw new InvalidOperationException("Сначала запустите INSTALL.bat.");
                        var start=new ProcessStartInfo(Path.Combine(InstalledRoot,"CountryProxy.exe"),string.Join(" ",args.Select(a=>"\""+a+"\"").ToArray())){UseShellExecute=false,CreateNoWindow=true};
                        using(var process=Process.Start(start)) {process.WaitForExit();return process.ExitCode;}
                    }
                    if(args.Contains("--enable")) {Enable();message="Разделение сайтов включено.\nVPN подключайте как обычно.";}
                    if(args.Contains("--disable")) {Disable();message="Разделение отключено.\nПрежние настройки прокси восстановлены.\nПерезапустите Thunderbird для применения его прежних настроек.";}
                    if(args.Contains("--status")) {
                        bool has=File.Exists(Path.Combine(Root,"state.json"));var state=has ? Read<State>("state.json") : null;var adapter=SelectAdapter();
                        bool enabled=has && Native.CurrentPac()==PacUrl(state),alive=has && Alive(state);
                        report=D("enabled",enabled,"worker_running",alive,"adapter",adapter==null ? null : adapter.Name);
                        message="Разделение: "+(enabled&&alive ? "включено" : "выключено")+"\nСеть: "+(adapter==null ? "нет подключения" : adapter.Name)+"\nПапка: "+Root;
                    }
                } else {message="Запустите INSTALL.bat для установки, ENABLE.bat для включения или DISABLE.bat для отключения.";}
                if(result!=null)File.WriteAllText(result,Json.Serialize(report ?? D("ok",true)),Utf8);
                if(!quiet && message!="")MessageBox.Show(message,"Разделение сайтов",MessageBoxButtons.OK,MessageBoxIcon.Information);
                return 0;
            } catch(Exception ex) {
                Log(ex.ToString());if(result!=null)File.WriteAllText(result,Json.Serialize(D("ok",false,"error",ex.Message)),Utf8);
                if(!quiet)MessageBox.Show(ex.Message,"Разделение сайтов",MessageBoxButtons.OK,MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
