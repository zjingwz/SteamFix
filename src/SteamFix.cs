﻿// SteamFix - 修复 Steam「无法连接到内容服务器」
// 编译: csc /target:exe /out:SteamFix.exe SteamFix.cs
// 运行环境: Windows 10/11 自带 .NET Framework 4.x, 免安装
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace SteamFix
{
    internal class Candidate
    {
        public string Host;
        public string IP;
        public bool TcpOk;
        public int ConnectMs = 9999;
        public bool HttpOk;
        public int HttpStatus;
        public int Score;
    }

    internal static class Program
    {
        private const string Ver = "1.0";
        private const string BeginMark = "# === SteamFix BEGIN (Steam CDN hosts) ===";
        private const string EndMark = "# === SteamFix END ===";
        private const int ConnectTimeoutMs = 2500;
        private const int ReadTimeoutMs = 3500;
        private const int MaxIPsPerHost = 30;
        private const int KeepPerHost = 2;

        private static bool DryRun;
        private static bool NoPause;
        private static bool DoRestore;
        private static string HostsFileOverride;
        private static readonly List<string> Log = new List<string>();

        private static readonly string[] CdnHosts = new string[]
        {
            "dl.steam.clngaa.com",
            "dl1.steam.clngaa.com",
            "st.dl.eccdnx.com",
            "xz.pphimalayanrt.com",
            "gstore.val.manlaxy.com",
            "gstore.val.smogfly.com"
        };

        private static readonly string[] DnsServers = new string[]
        {
            "223.5.5.5", "119.29.29.29", "180.76.76.76", "114.114.114.114"
        };

        // 备用种子(会实测过滤, 仅当公共 DNS 查不到时用)
        private static readonly string[] SeedPairs = new string[]
        {
            "dl.steam.clngaa.com=119.167.224.55,119.167.224.37,113.240.64.24",
            "dl1.steam.clngaa.com=36.99.183.119,27.18.13.139",
            "st.dl.eccdnx.com=125.44.162.112,125.44.162.149,221.204.95.143",
            "xz.pphimalayanrt.com=140.249.156.175,140.249.157.5",
            "gstore.val.manlaxy.com=140.249.156.196,140.249.156.190",
            "gstore.val.smogfly.com=140.249.156.196,140.249.156.190"
        };

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.Default;
            try { Console.Title = "SteamFix v" + Ver + " - Steam 下载修复工具"; }
            catch { }

            foreach (string a in args)
            {
                string t = a.Trim().ToLowerInvariant();
                if (t == "--dry-run" || t == "-n") DryRun = true;
                else if (t == "--nopause") NoPause = true;
                else if (t == "--restore" || t == "-r") DoRestore = true;
                else if (t.StartsWith("--hosts-file=")) HostsFileOverride = a.Substring(13).Trim('"');
                else if (t == "--help" || t == "-h" || t == "/?") { Help(); return 0; }
            }

            Banner();

            if (DoRestore)
            {
                if (!EnsureAdmin(args)) return 0;
                int removed = RemoveBlock();
                Say(removed > 0 ? "已删除 hosts 里的 Steam CDN 加速条目 (" + removed + " 行)。" : "hosts 里没有找到本工具添加的条目。", ConsoleColor.Green);
                FlushDns();
                Say("恢复完成。建议重启 Steam。", ConsoleColor.Green);
                SaveLog();
                Pause();
                return 0;
            }

            try
            {
                if (!EnsureAdmin(args)) return 0;

                Say("第 1 步 / 4  通过公共 DNS 解析 Steam 内容服务器域名 ...", ConsoleColor.Cyan);
                Dictionary<string, List<string>> names = ResolveAll();
                foreach (string h in CdnHosts)
                {
                    List<string> ips = names[h];
                    Say("   " + Pad(h, 26) + " -> " + ips.Count + " 个候选 IP" + (ips.Count == 0 ? "  (解析失败, 用备用种子)" : ""), ips.Count > 0 ? ConsoleColor.Gray : ConsoleColor.DarkYellow);
                }
                Console.WriteLine();

                Say("第 2 步 / 4  逐个实测哪些节点真的能连上并返回数据 ...", ConsoleColor.Cyan);
                List<Candidate> all = BuildCandidates(names);
                Say("   共 " + all.Count + " 个候选, 并发探测中 (最长约 " + Math.Max(5, all.Count / 40 + 6) + " 秒) ...", ConsoleColor.Gray);
                ProbeAll(all);

                int live = all.Count(delegate (Candidate c) { return c.HttpOk; });
                Say("   探测完成: " + live + " / " + all.Count + " 个节点可用。", live > 0 ? ConsoleColor.Gray : ConsoleColor.Red);
                Console.WriteLine();

                List<string> lines = PickBest(all);
                PrintTable(all, lines);
                Console.WriteLine();

                if (lines.Count == 0)
                {
                    Say("没能找到任何可用节点, 可能是当前网络对 Steam 内容服务器完全封锁。", ConsoleColor.Red);
                    Say("建议改用游戏加速器 (雷神/UU/奇游 等), 或换个网络再试。", ConsoleColor.Yellow);
                    SaveLog();
                    Pause();
                    return 2;
                }

                Say("第 3 步 / 4  写入 hosts 文件 ...", ConsoleColor.Cyan);
                if (DryRun)
                {
                    Say("   [试运行模式] 不会真的修改 hosts。将要写入的内容:", ConsoleColor.Yellow);
                    foreach (string l in lines) Say("      " + l, ConsoleColor.Yellow);
                }
                else
                {
                    string backup = BackupHosts();
                    Say("   已备份原 hosts -> " + backup, ConsoleColor.Gray);
                    WriteBlock(lines);
                    Say("   已写入 " + lines.Count + " 条记录", ConsoleColor.Green);
                    FlushDns();
                    Say("   已刷新 DNS 缓存", ConsoleColor.Gray);
                }
                Console.WriteLine();

                Say("第 4 步 / 4  重启 Steam ...", ConsoleColor.Cyan);
                string steamExe = FindSteam();
                if (steamExe == null)
                {
                    Say("   没找到 Steam 安装目录, 请手动关闭并重新打开 Steam。", ConsoleColor.Yellow);
                }
                else if (DryRun)
                {
                    Say("   [试运行模式] 跳过重启 Steam (" + steamExe + ")", ConsoleColor.Yellow);
                }
                else if (Ask("   找到 Steam: " + steamExe + "\r\n   现在自动重启 Steam 吗? (Y/N)"))
                {
                    RestartSteam(steamExe);
                }
                else
                {
                    Say("   已跳过。请手动重启 Steam 让设置生效。", ConsoleColor.Yellow);
                }

                Console.WriteLine();
                Say("完成! 打开 Steam 应该就能正常下载了。", ConsoleColor.Green);
                Say("如果以后又出现同样问题 (CDN 节点会变动), 重新运行本工具即可。", ConsoleColor.Gray);
                Say("想撤销改动: 运行 SteamFix.exe --restore", ConsoleColor.DarkGray);
                SaveLog();
                Pause();
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Say("出错了: " + ex.Message, ConsoleColor.Red);
                Say(ex.ToString(), ConsoleColor.DarkGray);
                SaveLog();
                Pause();
                return 1;
            }
        }

        // ---------- 界面 ----------

        private static void Banner()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==========================================================");
            Console.WriteLine("   SteamFix v" + Ver + "   Steam 下载修复工具");
            Console.WriteLine("   解决「无法连接到内容服务器 / 下载卡住 / 速度 0 bps」");
            Console.WriteLine("==========================================================");
            Console.ResetColor();
            Console.WriteLine();
        }

        private static void Help()
        {
            Console.WriteLine("SteamFix v" + Ver + " - 修复 Steam 无法连接到内容服务器");
            Console.WriteLine();
            Console.WriteLine("  SteamFix.exe              探测可用 CDN 节点并写入 hosts (需要管理员)");
            Console.WriteLine("  SteamFix.exe --dry-run    只探测不修改 (看看有哪些节点可用)");
            Console.WriteLine("  SteamFix.exe --restore    删除本工具添加到 hosts 的内容");
            Console.WriteLine("  SteamFix.exe --nopause    结束后不等待按键");
            Console.WriteLine();
            Console.WriteLine("原理: Steam 下载时会把分片请求重定向到 CDN 节点, 但校园网/运营商");
            Console.WriteLine("      的 DNS 常常返回一大堆已失效的 IP, Steam 挨个超时后报错。");
            Console.WriteLine("      本工具用公共 DNS 拿到候选 IP, 逐个实测, 把真正可用的写进 hosts。");
        }

        private static void Say(string s, ConsoleColor c)
        {
            Console.ForegroundColor = c;
            Console.WriteLine(s);
            Console.ResetColor();
            Log.Add(s);
        }

        private static void Pause()
        {
            if (NoPause) return;
            try
            {
                if (Console.IsInputRedirected) return;
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("按任意键退出 ...");
                Console.ResetColor();
                Console.ReadKey(true);
                Console.WriteLine();
            }
            catch { }
        }

        private static bool Ask(string q)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(q);
            Console.ResetColor();
            try
            {
                if (Console.IsInputRedirected) return false;
                Console.Write("请输入 (Y/N): ");
                string s = Console.ReadLine();
                if (s == null) return false;
                s = s.Trim().ToLowerInvariant();
                return s == "y" || s == "yes" || s == "是";
            }
            catch { return false; }
        }

        private static string Pad(string s, int n)
        {
            if (s.Length >= n) return s;
            return s + new string(' ', n - s.Length);
        }

        // ---------- 管理员 ----------

        private static bool IsAdmin()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static bool EnsureAdmin(string[] args)
        {
            if (IsAdmin()) return true;
            if (HostsFileOverride != null)
            {
                Say("提示: 指定了自定义 hosts 文件, 跳过提权。", ConsoleColor.DarkGray);
                return true;
            }
            if (DryRun)
            {
                Say("提示: 试运行模式不修改任何文件, 无需管理员权限。", ConsoleColor.DarkGray);
                return true;
            }
            Say("需要管理员权限来修改 hosts 文件, 正在申请提权 ...", ConsoleColor.Yellow);
            Say("请在弹窗里点『是』。", ConsoleColor.Yellow);
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Assembly.GetExecutingAssembly().Location;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.Arguments = string.Join(" ", args);
                psi.WorkingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                Process.Start(psi);
                return false;
            }
            catch (Exception ex)
            {
                Say("提权被取消或失败: " + ex.Message, ConsoleColor.Red);
                Say("请右键 SteamFix.exe -> 以管理员身份运行。", ConsoleColor.Yellow);
                Pause();
                return false;
            }
        }

        // ---------- DNS ----------

        private static Dictionary<string, List<string>> ResolveAll()
        {
            Dictionary<string, List<string>> map = new Dictionary<string, List<string>>();
            foreach (string h in CdnHosts) map[h] = new List<string>();

            foreach (string server in DnsServers)
            {
                bool any = false;
                foreach (string h in CdnHosts)
                {
                    List<string> got = DnsQueryA(h, server, 3000);
                    if (got.Count > 0) any = true;
                    foreach (string ip in got) if (!map[h].Contains(ip)) map[h].Add(ip);
                }
                if (any && map.Values.All(delegate (List<string> v) { return v.Count > 0; })) break;
            }

            // 系统解析器补充
            foreach (string h in CdnHosts)
            {
                try
                {
                    IPAddress[] arr = Dns.GetHostAddresses(h);
                    foreach (IPAddress a in arr)
                        if (a.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string s = a.ToString();
                            if (!map[h].Contains(s)) map[h].Add(s);
                        }
                }
                catch { }
            }

            // 备用种子
            foreach (string pair in SeedPairs)
            {
                int eq = pair.IndexOf('=');
                string h = pair.Substring(0, eq);
                string[] ips = pair.Substring(eq + 1).Split(',');
                if (!map.ContainsKey(h)) continue;
                if (map[h].Count > 0) continue; // 只在解析失败时兜底
                foreach (string ip in ips) map[h].Add(ip.Trim());
            }

            return map;
        }

        private static byte[] BuildQuery(string domain)
        {
            List<byte> b = new List<byte>();
            int id = Math.Abs(Guid.NewGuid().GetHashCode()) % 65535;
            b.Add((byte)(id >> 8)); b.Add((byte)(id & 0xFF));
            b.Add(0x01); b.Add(0x00);
            b.Add(0x00); b.Add(0x01);
            b.Add(0x00); b.Add(0x00);
            b.Add(0x00); b.Add(0x00);
            b.Add(0x00); b.Add(0x00);
            foreach (string part in domain.Split('.'))
            {
                byte[] p = Encoding.ASCII.GetBytes(part);
                b.Add((byte)p.Length);
                b.AddRange(p);
            }
            b.Add(0x00);
            b.Add(0x00); b.Add(0x01);
            b.Add(0x00); b.Add(0x01);
            return b.ToArray();
        }

        private static int SkipName(byte[] buf, int pos)
        {
            while (pos < buf.Length)
            {
                byte len = buf[pos];
                if (len == 0) return pos + 1;
                if ((len & 0xC0) == 0xC0) return pos + 2;
                pos += 1 + len;
            }
            return pos;
        }

        private static List<string> ParseAnswers(byte[] buf)
        {
            List<string> res = new List<string>();
            if (buf == null || buf.Length < 12) return res;
            int qd = (buf[4] << 8) | buf[5];
            int an = (buf[6] << 8) | buf[7];
            int pos = 12;
            for (int i = 0; i < qd; i++)
            {
                pos = SkipName(buf, pos) + 4;
                if (pos > buf.Length) return res;
            }
            for (int i = 0; i < an; i++)
            {
                if (pos >= buf.Length) break;
                pos = SkipName(buf, pos);
                if (pos + 10 > buf.Length) break;
                int type = (buf[pos] << 8) | buf[pos + 1];
                int rdlen = (buf[pos + 8] << 8) | buf[pos + 9];
                pos += 10;
                if (pos + rdlen > buf.Length) break;
                if (type == 1 && rdlen == 4)
                    res.Add(buf[pos] + "." + buf[pos + 1] + "." + buf[pos + 2] + "." + buf[pos + 3]);
                pos += rdlen;
            }
            return res;
        }

        private static List<string> DnsQueryA(string domain, string server, int timeoutMs)
        {
            List<string> res = new List<string>();
            UdpClient udp = null;
            try
            {
                udp = new UdpClient();
                udp.Client.ReceiveTimeout = timeoutMs;
                udp.Connect(server, 53);
                byte[] q = BuildQuery(domain);
                udp.Send(q, q.Length);
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] resp = udp.Receive(ref ep);
                res = ParseAnswers(resp);
            }
            catch { }
            finally { if (udp != null) { try { udp.Close(); } catch { } } }
            return res;
        }

        // ---------- 探测 ----------

        private static List<Candidate> BuildCandidates(Dictionary<string, List<string>> names)
        {
            List<Candidate> all = new List<Candidate>();
            foreach (string h in CdnHosts)
            {
                List<string> ips = names[h];
                int n = 0;
                foreach (string ip in ips)
                {
                    if (n++ >= MaxIPsPerHost) break;
                    Candidate c = new Candidate();
                    c.Host = h;
                    c.IP = ip;
                    all.Add(c);
                }
            }
            return all;
        }

        private static void ProbeAll(List<Candidate> all)
        {
            List<Task> tasks = new List<Task>();
            foreach (Candidate c in all)
            {
                Candidate local = c;
                tasks.Add(Task.Factory.StartNew(delegate { Probe(local); }));
            }
            try { Task.WaitAll(tasks.ToArray(), 90000); }
            catch { }
        }

        private static void Probe(Candidate c)
        {
            TcpClient tcp = new TcpClient();
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                IAsyncResult ar = tcp.BeginConnect(c.IP, 80, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs, false))
                {
                    try { tcp.Close(); } catch { }
                    return;
                }
                tcp.EndConnect(ar);
                c.TcpOk = true;
                c.ConnectMs = (int)sw.ElapsedMilliseconds;

                NetworkStream ns = tcp.GetStream();
                ns.ReadTimeout = ReadTimeoutMs;
                ns.WriteTimeout = ReadTimeoutMs;
                string req = "GET / HTTP/1.1\r\nHost: " + c.Host + "\r\nUser-Agent: SteamFix/" + Ver + "\r\nAccept: */*\r\nConnection: close\r\n\r\n";
                byte[] rb = Encoding.ASCII.GetBytes(req);
                ns.Write(rb, 0, rb.Length);
                ns.Flush();

                byte[] buf = new byte[1024];
                int n = ns.Read(buf, 0, buf.Length);
                if (n > 0)
                {
                    c.HttpOk = true;
                    string head = Encoding.ASCII.GetString(buf, 0, n);
                    int sp = head.IndexOf(' ');
                    if (sp > 0 && head.Length >= sp + 4)
                    {
                        int code;
                        if (int.TryParse(head.Substring(sp + 1, 3), out code)) c.HttpStatus = code;
                    }
                }
            }
            catch { }
            finally { try { tcp.Close(); } catch { } }

            if (c.HttpOk) c.Score = 10000 - Math.Min(3000, c.ConnectMs);
            else if (c.TcpOk) c.Score = 1000 - Math.Min(900, c.ConnectMs);
            else c.Score = -1;
        }

        private static List<string> PickBest(List<Candidate> all)
        {
            List<string> lines = new List<string>();
            foreach (string h in CdnHosts)
            {
                string host = h;
                List<Candidate> grp = all.Where(delegate (Candidate c) { return c.Host == host && c.HttpOk; }).ToList();
                if (grp.Count == 0)
                    grp = all.Where(delegate (Candidate c) { return c.Host == host && c.TcpOk; }).ToList();
                grp = grp.OrderByDescending(delegate (Candidate c) { return c.Score; }).Take(KeepPerHost).ToList();
                foreach (Candidate c in grp)
                    lines.Add(Pad(c.IP, 17) + c.Host);
            }
            return lines;
        }

        private static void PrintTable(List<Candidate> all, List<string> chosen)
        {
            Say("   实测结果 (只列能连上的):", ConsoleColor.Cyan);
            List<Candidate> ok = all.Where(delegate (Candidate c) { return c.TcpOk; })
                                   .OrderBy(delegate (Candidate c) { return c.Host; })
                                   .ThenByDescending(delegate (Candidate c) { return c.Score; })
                                   .ToList();
            if (ok.Count == 0) { Say("     (无)", ConsoleColor.Red); return; }

            HashSet<string> chosenSet = new HashSet<string>();
            foreach (string l in chosen) chosenSet.Add(l);

            foreach (Candidate c in ok)
            {
                bool sel = chosenSet.Contains(Pad(c.IP, 17) + c.Host);
                string state = c.HttpOk ? ("HTTP " + (c.HttpStatus > 0 ? c.HttpStatus.ToString() : "?") + " 正常") : "仅端口通";
                Console.ForegroundColor = sel ? ConsoleColor.Green : (c.HttpOk ? ConsoleColor.Gray : ConsoleColor.DarkGray);
                Console.WriteLine("     " + (sel ? "[采用] " : "       ") + Pad(c.Host, 26) + Pad(c.IP, 17) + Pad(c.ConnectMs + "ms", 9) + state);
                Console.ResetColor();
                Log.Add("     " + (sel ? "[采用] " : "       ") + Pad(c.Host, 26) + Pad(c.IP, 17) + Pad(c.ConnectMs + "ms", 9) + state);
            }
        }

        // ---------- hosts ----------

        private static string HostsPath()
        {
            if (HostsFileOverride != null) return HostsFileOverride;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers\\etc\\hosts");
        }

        private static string[] StripBlock(string[] src, out int removed)
        {
            List<string> outl = new List<string>();
            bool skip = false;
            removed = 0;
            foreach (string l in src)
            {
                string t = l.Trim();
                if (t == BeginMark) { skip = true; removed++; continue; }
                if (t == EndMark) { skip = false; removed++; continue; }
                if (skip) { removed++; continue; }
                outl.Add(l);
            }
            return outl.ToArray();
        }

        // 保留原 hosts 的编码: UTF-8 BOM / UTF-16 -> 原样; 否则用系统 ANSI(中文系统为 GBK)
        private static Encoding EncOf(string path)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) return new UTF8Encoding(true);
                if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) return Encoding.Unicode;
                if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF) return Encoding.BigEndianUnicode;
            }
            catch { }
            return Encoding.Default;
        }

        private static int RemoveBlock()
        {
            string p = HostsPath();
            try { File.SetAttributes(p, FileAttributes.Normal); } catch { }
            Encoding enc = EncOf(p);
            string[] src = File.ReadAllLines(p, enc);
            int removed;
            string[] outl = StripBlock(src, out removed);
            File.WriteAllLines(p, outl, enc);
            return removed;
        }

        private static string BackupHosts()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamFix");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string dst = Path.Combine(dir, "hosts.bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            File.Copy(HostsPath(), dst, true);
            return dst;
        }

        private static void WriteBlock(List<string> lines)
        {
            string p = HostsPath();
            try { File.SetAttributes(p, FileAttributes.Normal); } catch { }
            Encoding enc = EncOf(p);
            string[] src = File.ReadAllLines(p, enc);
            int removed;
            string[] outl = StripBlock(src, out removed);

            List<string> final = new List<string>(outl);
            while (final.Count > 0 && final[final.Count - 1].Trim() == "") final.RemoveAt(final.Count - 1);
            final.Add("");
            final.Add(BeginMark);
            final.Add("# Generated by SteamFix v" + Ver + " at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            final.Add("# SteamFix.exe --restore   removes this block.");
            foreach (string l in lines) final.Add(l);
            final.Add(EndMark);
            final.Add("");

            File.WriteAllLines(p, final.ToArray(), enc);
        }

        private static void FlushDns()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("ipconfig", "/flushdns");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                Process pr = Process.Start(psi);
                pr.StandardOutput.ReadToEnd();
                pr.WaitForExit(10000);
            }
            catch { }
        }

        // ---------- Steam ----------

        private static string FindSteam()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    if (k != null) { object v = k.GetValue("SteamPath"); if (v != null) { string s = Path.Combine(v.ToString().Replace('/', '\\'), "steam.exe"); if (File.Exists(s)) return s; } }
            }
            catch { }
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    if (k != null) { object v = k.GetValue("InstallPath"); if (v != null) { string s = Path.Combine(v.ToString().Replace('/', '\\'), "steam.exe"); if (File.Exists(s)) return s; } }
            }
            catch { }
            string[] guess = new string[]
            {
                @"C:\Program Files (x86)\Steam\steam.exe",
                @"C:\Program Files\Steam\steam.exe",
                @"D:\Steam\steam.exe",
                @"E:\Steam\steam.exe"
            };
            foreach (string g in guess) if (File.Exists(g)) return g;
            return null;
        }

        private static void RestartSteam(string steamExe)
        {
            try
            {
                Process[] ps = Process.GetProcessesByName("steam");
                if (ps.Length > 0)
                {
                    Say("   正在关闭 Steam ...", ConsoleColor.Gray);
                    ProcessStartInfo si = new ProcessStartInfo(steamExe, "-shutdown");
                    si.UseShellExecute = false;
                    try { Process.Start(si); } catch { }
                    for (int i = 0; i < 40; i++)
                    {
                        System.Threading.Thread.Sleep(1500);
                        if (Process.GetProcessesByName("steam").Length == 0) break;
                    }
                }
                Say("   正在启动 Steam ...", ConsoleColor.Gray);
                ProcessStartInfo st = new ProcessStartInfo(steamExe);
                st.UseShellExecute = true;
                st.WorkingDirectory = Path.GetDirectoryName(steamExe);
                Process.Start(st);
                Say("   Steam 已重启。", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                Say("   自动重启失败 (" + ex.Message + "), 请手动重启 Steam。", ConsoleColor.Yellow);
            }
        }

        // ---------- 日志 ----------

        private static void SaveLog()
        {
            if (DryRun) return;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamFix");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, "steamfix.log");
                List<string> sb = new List<string>();
                sb.Add("========== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " SteamFix v" + Ver + " ==========");
                sb.AddRange(Log);
                sb.Add("");
                File.AppendAllLines(p, sb.ToArray(), new UTF8Encoding(true));
            }
            catch { }
        }
    }
}
