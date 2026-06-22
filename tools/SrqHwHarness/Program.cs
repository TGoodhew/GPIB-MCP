using System;
using System.Diagnostics;
using System.Threading;
using GpibMcp.Instruments;
using Srq.Completion;

namespace SrqHwHarness
{
    /// <summary>
    /// Live-hardware harness for the SRQ completion pattern. Drives the REAL <see cref="CompletionWaiter"/>
    /// against a REAL instrument over NI-VISA, with no Claude Desktop and no MCP/stdio layer in the path.
    ///
    /// It deliberately reuses the exact production pieces so a green run here means the server's
    /// instrument_wait_complete tool will behave identically:
    ///   - I/O:         <see cref="InstrumentManager"/> (the same class the server uses)
    ///   - statusModel: the bundled + user instrument database (<see cref="InstrumentDatabase"/>)
    ///   - resolution:  resource -> assignment -> model, same as the WaitComplete adapter
    ///   - waiter:      <see cref="CompletionWaiter.Wait"/>, traced to the console
    ///
    /// Usage:
    ///   SrqHwHarness --list
    ///   SrqHwHarness &lt;resource&gt; [operation] [--model M] [--setup "CMDS;"] [--timeout ms] [--poll ms]
    ///
    /// Examples (the bench cases from the SRQ epic):
    ///   SrqHwHarness GPIB0::18::INSTR sweepComplete --setup "CF 300MHZ;SP 100MHZ;ST 5S;"
    ///   SrqHwHarness GPIB0::18::INSTR sweepAndPeak  --model 8563E --timeout 60000
    ///   SrqHwHarness GPIB0::10::INSTR sweepComplete --model 3325A
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (GpibOperationException gex)
            {
                Console.Error.WriteLine("GPIB/VISA error: " + gex.Detail);
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            // Diagnostic mode: `raw <resource> --step "send ..." --step "spoll" ...`
            // Lets the SRQ flow be characterized against real hardware step by step, decoding every
            // serial poll under BOTH 8560 bit layouts (RQS-mask vs STB read-back) so the behaviour
            // can be confirmed without guessing. See srq-8560-dual-bit-layout.
            if (args.Length > 0 && (args[0] == "raw" || args[0] == "probe"))
                return RunRaw(args);

            var opt = Options.Parse(args);
            if (opt == null) { PrintUsage(); return 1; }

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;

            if (opt.List)
            {
                using (var visaList = new InstrumentManager(new NiVisaTransport()))
                {
                    var found = visaList.ListResources(null);
                    Console.WriteLine("VISA resources found: " + found.Count);
                    foreach (var r in found) Console.WriteLine("  " + r);
                }
                return 0;
            }

            // ---- resolve model + statusModel exactly like the server's WaitComplete ----
            var db = InstrumentDatabase.Load(InstrumentPaths.DatabaseDirectories(exeDir));
            var assignments = AssignmentStore.FromFile(InstrumentPaths.BindingsPath());

            string model = opt.Model ?? assignments.Get(opt.Resource);
            if (string.IsNullOrEmpty(model))
            {
                Console.Error.WriteLine("No model for " + opt.Resource +
                    ". Pass --model <name> or assign one in " + InstrumentPaths.BindingsPath() + ".");
                return 1;
            }

            InstrumentDefinition def;
            if (!db.TryGet(model, out def))
            {
                Console.Error.WriteLine("Unknown model '" + model + "' (not in the instrument database).");
                return 1;
            }

            Console.WriteLine("=== SRQ hardware harness - real instrument over NI-VISA, no Claude Desktop ===");
            Console.WriteLine("  resource : " + opt.Resource);
            Console.WriteLine("  model    : " + model + (opt.Model != null ? " (override)" : " (from assignment)"));
            Console.WriteLine("  operation: " + opt.Operation);
            Console.WriteLine("  timeout  : " + opt.TimeoutMs + " ms, poll " + opt.PollMs + " ms");
            Console.WriteLine();

            using (var visa = new InstrumentManager(new NiVisaTransport()))
            {
                if (!string.IsNullOrEmpty(opt.Setup))
                {
                    Console.WriteLine("setup -> " + opt.Setup);
                    visa.Write(opt.Resource, opt.Setup, InstrumentManager.DefaultTimeoutMs);
                }

                // Same adapter the server's WaitComplete uses: Send=Write, SerialPoll=ReadStatusByte.
                var channel = new VisaStatusChannel(visa, opt.Resource);
                var watch = Stopwatch.StartNew();

                CompletionResult result = CompletionWaiter.Wait(
                    def.StatusModel, model, opt.Operation, opt.TimeoutMs, channel,
                    () => watch.ElapsedMilliseconds, Thread.Sleep,
                    pollIntervalMs: opt.PollMs,
                    trace: line => Console.WriteLine("   " + line));

                Console.WriteLine();
                Console.WriteLine("RESULT: " + result.Outcome + ", elapsed " + result.ElapsedMs +
                                  " ms, status 0x" + result.StatusByte.ToString("X2"));
                Console.WriteLine(result.Message);
                return ExitCode(result.Outcome);
            }
        }

        // ---- diagnostic / probe mode ------------------------------------------------
        // Each --step is "VERB args":
        //   send <cmds>     write commands (e.g. send SNGLS;TS;)
        //   query <cmd>     write + read, print the response (e.g. query ERR?)
        //   spoll           serial poll (NI viReadSTB), print hex + decode under both 8560 layouts
        //   stb             send "STB?" and print the instrument's own status-byte number
        //   clear           device clear
        //   sleep <ms>      wait
        // Example confirming the dual-layout theory on the 8563E:
        //   raw GPIB0::18::INSTR --step "send IP;SNGLS;" --step "query ERR?" --step "spoll"
        //       --step "send RQS 80;TS;" --step "spoll" --step "send RQS 0;CONTS;"
        private static int RunRaw(string[] args)
        {
            string resource = null;
            var steps = new System.Collections.Generic.List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--step") { if (++i < args.Length) steps.Add(args[i]); }
                else if (!args[i].StartsWith("--") && resource == null) resource = args[i];
            }
            if (string.IsNullOrEmpty(resource) || steps.Count == 0)
            {
                Console.Error.WriteLine("Usage: SrqHwHarness raw <resource> --step \"send CMDS\" --step \"spoll\" ...");
                return 1;
            }

            Console.WriteLine("=== SRQ raw probe: " + resource + " ===");
            var clock = Stopwatch.StartNew();
            using (var visa = new InstrumentManager(new NiVisaTransport()))
            {
                foreach (var step in steps)
                {
                    string verb, rest;
                    int sp = step.IndexOf(' ');
                    if (sp < 0) { verb = step; rest = ""; } else { verb = step.Substring(0, sp); rest = step.Substring(sp + 1); }

                    long t0 = clock.ElapsedMilliseconds;
                    Console.Write("[t=" + t0 + "ms] ");
                    switch (verb)
                    {
                        case "send":
                            visa.Write(resource, rest, InstrumentManager.DefaultTimeoutMs);
                            Console.WriteLine("send  -> " + rest);
                            break;
                        case "query":
                            string resp = visa.Query(resource, rest, InstrumentManager.DefaultTimeoutMs);
                            Console.WriteLine("query -> " + rest + "  =>  " + (resp == null ? "<null>" : resp.Trim()));
                            break;
                        case "stb":
                            string stbResp = visa.Query(resource, "STB?", InstrumentManager.DefaultTimeoutMs);
                            Console.WriteLine("STB?  -> " + DecodeBoth(ParseInt(stbResp)));
                            break;
                        case "spoll":
                            int stb = visa.SerialPoll(resource);
                            Console.WriteLine("spoll -> " + DecodeBoth(stb));
                            break;
                        case "watch":   // watch <intervalMs>x<count> - poll until request-service (0x40) or count
                        {
                            int xi = rest.IndexOf('x');
                            int interval = ParseInt(xi < 0 ? rest : rest.Substring(0, xi));
                            int count = xi < 0 ? 1 : ParseInt(rest.Substring(xi + 1));
                            if (interval <= 0) interval = 100;
                            Console.WriteLine("watch " + interval + "ms x" + count);
                            for (int n = 0; n < count; n++)
                            {
                                int s = visa.SerialPoll(resource);
                                Console.WriteLine("   [t=" + clock.ElapsedMilliseconds + "ms] " + DecodeBoth(s));
                                if ((s & 0x40) != 0) { Console.WriteLine("   request-service asserted - stop."); break; }
                                Thread.Sleep(interval);
                            }
                            break;
                        }
                        case "clear":
                            visa.Clear(resource, InstrumentManager.DefaultTimeoutMs);
                            Console.WriteLine("clear (device clear)");
                            break;
                        case "srq":   // prototype of the robust waiter: srq <armMask> <armCmds...>
                        {              // edge handshake: arm -> start -> wait BUSY (expect drops) -> wait DONE (RQS)
                            int sps = rest.IndexOf(' ');
                            int armMask = ParseInt(sps < 0 ? rest : rest.Substring(0, sps));
                            string armCmds = sps < 0 ? "TS;" : rest.Substring(sps + 1);
                            const int RQS = 0x40, EXPECT = 0x10, ERR = 0x20; // 8563E read-back layout
                            visa.Write(resource, "RQS 0", InstrumentManager.DefaultTimeoutMs);
                            visa.SerialPoll(resource); // best-effort drain
                            visa.Write(resource, "RQS " + armMask, InstrumentManager.DefaultTimeoutMs);
                            visa.Write(resource, armCmds, InstrumentManager.DefaultTimeoutMs);
                            Console.WriteLine("srq armed RQS " + armMask + ", started '" + armCmds + "'");
                            long tStart = clock.ElapsedMilliseconds;
                            bool busy = false; int last = -1;
                            for (int n = 0; n < 200; n++)
                            {
                                int s = visa.SerialPoll(resource);
                                if (s != last) { Console.WriteLine("   [t=" + clock.ElapsedMilliseconds + "ms] " + DecodeBoth(s)); last = s; }
                                if (!busy) { if ((s & EXPECT) == 0) { busy = true; Console.WriteLine("   -> BUSY confirmed (expect cleared)"); } }
                                else if ((s & RQS) != 0)
                                {
                                    string verdict = (s & ERR) != 0 ? "INSTRUMENT ERROR" : "COMPLETED";
                                    Console.WriteLine("   -> " + verdict + " after " + (clock.ElapsedMilliseconds - tStart) + " ms");
                                    break;
                                }
                                if (clock.ElapsedMilliseconds - tStart > 30000) { Console.WriteLine("   -> TIMEOUT"); break; }
                                Thread.Sleep(200);
                            }
                            visa.Write(resource, "RQS 0;CONTS;", InstrumentManager.DefaultTimeoutMs);
                            break;
                        }
                        case "sleep":
                            int ms = ParseInt(rest);
                            Thread.Sleep(ms > 0 ? ms : 0);
                            Console.WriteLine("sleep " + ms + " ms");
                            break;
                        default:
                            Console.WriteLine("(unknown step: " + step + ")");
                            break;
                    }
                }
            }
            return 0;
        }

        // The two competing 8560-series layouts (8560E Programming Guide):
        //   mask  = Table 7-9, the value you WRITE in RQS <mask>
        //   read  = Table 7-266 (STB?) / GPIB serial-poll convention, the value you READ BACK
        private static readonly string[] MaskLayout = BitNames(  // by bit index 0..7
            null, null, "trigger", "message", "endOfSweep", "commandComplete", "ERROR-PRESENT", "RQS");
        private static readonly string[] ReadLayout = BitNames(
            null, "message", "endOfSweep", null, "commandComplete", "errorPresent", "REQUEST-SERVICE", null);

        private static string[] BitNames(params string[] names) => names;

        private static string DecodeBoth(int stb) =>
            "0x" + stb.ToString("X2") + " (" + stb + ")  | RQS-mask layout: [" + Decode(stb, MaskLayout) +
            "]  | STB read-back layout: [" + Decode(stb, ReadLayout) + "]";

        private static string Decode(int stb, string[] layout)
        {
            var set = new System.Collections.Generic.List<string>();
            for (int b = 7; b >= 0; b--)
                if ((stb & (1 << b)) != 0)
                    set.Add(layout[b] != null ? layout[b] : ("bit" + b));
            return set.Count > 0 ? string.Join(", ", set) : "none";
        }

        private static int ParseInt(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            return int.TryParse(s, out int v) ? v : 0;
        }

        private static int ExitCode(CompletionOutcome outcome)
        {
            switch (outcome)
            {
                case CompletionOutcome.Completed: return 0;
                case CompletionOutcome.InstrumentError: return 2;
                case CompletionOutcome.TimedOut: return 3;
                default: return 4; // Refused / NeedsDefinition
            }
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine(
                "Usage:\n" +
                "  SrqHwHarness --list\n" +
                "  SrqHwHarness <resource> [operation] [--model M] [--setup \"CMDS;\"] [--timeout ms] [--poll ms]\n\n" +
                "  operation defaults to 'sweepComplete'; timeout 30000 ms; poll 500 ms.\n\n" +
                "Examples:\n" +
                "  SrqHwHarness GPIB0::18::INSTR sweepComplete --setup \"CF 300MHZ;SP 100MHZ;ST 5S;\"\n" +
                "  SrqHwHarness GPIB0::18::INSTR sweepAndPeak --model 8563E --timeout 60000\n" +
                "  SrqHwHarness GPIB0::10::INSTR sweepComplete --model 3325A");
        }

        /// <summary>Bridges <see cref="IStatusChannel"/> to real VISA I/O - identical to the server's adapter.</summary>
        private sealed class VisaStatusChannel : IStatusChannel
        {
            private readonly InstrumentManager _visa;
            private readonly string _resource;
            public VisaStatusChannel(InstrumentManager visa, string resource) { _visa = visa; _resource = resource; }
            public void Send(string command) => _visa.Write(_resource, command, InstrumentManager.DefaultTimeoutMs);
            public int SerialPoll() => _visa.SerialPoll(_resource);
        }

        private sealed class Options
        {
            public bool List;
            public string Resource;
            public string Operation = "sweepComplete";
            public string Model;
            public string Setup;
            public int TimeoutMs = 30000;
            public int PollMs = 500;

            public static Options Parse(string[] args)
            {
                var o = new Options();
                int positional = 0;
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    switch (a)
                    {
                        case "--list": o.List = true; break;
                        case "--model": o.Model = Next(args, ref i); break;
                        case "--setup": o.Setup = Next(args, ref i); break;
                        case "--timeout": o.TimeoutMs = Int(Next(args, ref i), o.TimeoutMs); break;
                        case "--poll": o.PollMs = Int(Next(args, ref i), o.PollMs); break;
                        case "-h": case "--help": case "/?": return null;
                        default:
                            if (a.StartsWith("--")) return null;
                            if (positional == 0) { o.Resource = a; positional++; }
                            else if (positional == 1) { o.Operation = a; positional++; }
                            else return null;
                            break;
                    }
                }
                if (o.List) return o;
                return string.IsNullOrEmpty(o.Resource) ? null : o;
            }

            private static string Next(string[] args, ref int i) => (++i < args.Length) ? args[i] : null;
            private static int Int(string s, int fallback) =>
                int.TryParse(s, out int v) && v > 0 ? v : fallback;
        }
    }
}
