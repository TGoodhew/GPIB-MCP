using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static GpibMcp.Tools.ToolArgs;

namespace GpibMcp.Tools
{
    /// <summary>
    /// Tools backed by the instrument command database and the address-to-model assignment
    /// store: discovering what models are known, reading a model's command reference,
    /// identifying/assigning instruments, and extending the database.
    /// </summary>
    public static class DatabaseTools
    {
        private const int VisaTimeoutMs = VisaInstrumentManager.DefaultTimeoutMs;

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        };

        public static void Register(ToolRegistry registry, InstrumentDatabase db,
                                    AssignmentStore assignments, IInstrumentManager visa,
                                    string bundledDir = null)
        {
            // Where the shipped definitions live (next to the exe in production); injectable for tests.
            bundledDir = bundledDir ?? InstrumentPaths.BundledDatabaseDir(
                Path.GetDirectoryName(typeof(DatabaseTools).Assembly.Location));

            // ---- What instruments do you know about? ----------------------------
            registry.Add(new McpTool(
                "instrument_list_models",
                "List the GPIB/VISA instrument models in the local command database - i.e. which " +
                "instruments this server knows the commands for. Use this to answer questions like " +
                "\"what instruments do you know about?\".",
                Schema(),
                args =>
                {
                    var all = db.All;
                    if (all.Count == 0)
                        return "The instrument database is empty. Add models with instrument_db_save.";
                    var sb = new StringBuilder();
                    sb.AppendLine(all.Count + " instrument model(s) known:");
                    foreach (var d in all.OrderBy(x => x.Model, StringComparer.OrdinalIgnoreCase))
                    {
                        int cmds = d.Commands != null ? d.Commands.Count : 0;
                        string idc = d.Identity != null ? d.Identity.Command : null;
                        sb.AppendLine("  " + d.Model +
                            " - " + (d.Category ?? "?") +
                            " (" + (d.Manufacturer ?? "?") + "), " +
                            cmds + " commands" +
                            (string.IsNullOrEmpty(idc) ? "" : ", id: " + idc));
                    }
                    return sb.ToString().TrimEnd();
                }));

            // ---- Command reference for a model ----------------------------------
            registry.Add(new McpTool(
                "instrument_reference",
                "Get a model's command reference from the database so you can work out what it can do. " +
                "With no command/search, returns metadata plus a compact command index; pass command=<name " +
                "or mnemonic> for full detail of one command, or search=/category= to filter.",
                Schema(
                    Required("model", "string", "Model name or alias, e.g. '8563E'."),
                    Prop("command", "string", "Return full detail for this command (name or mnemonic)."),
                    Prop("search", "string", "Filter commands by text in name/mnemonic/description."),
                    Prop("category", "string", "Filter commands by category.")),
                args =>
                {
                    string model = ReqStr(args, "model");
                    InstrumentDefinition def;
                    if (!db.TryGet(model, out def))
                        return "Unknown model '" + model + "'. Use instrument_list_models to see known models.";

                    string command = Str(args, "command", null);
                    if (!string.IsNullOrEmpty(command))
                    {
                        var cmd = FindCommand(def, command);
                        if (cmd == null)
                            return "Model '" + def.Model + "' has no command matching '" + command + "'.";
                        return JsonConvert.SerializeObject(cmd, JsonSettings);
                    }

                    IEnumerable<InstrumentCommand> cmds = def.Commands ?? new List<InstrumentCommand>();
                    string category = Str(args, "category", null);
                    if (!string.IsNullOrEmpty(category))
                        cmds = cmds.Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase));
                    string search = Str(args, "search", null);
                    if (!string.IsNullOrEmpty(search))
                        cmds = cmds.Where(c => ContainsCI(c.Name, search) || ContainsCI(c.Mnemonic, search) ||
                                               ContainsCI(c.Description, search));
                    var list = cmds.ToList();

                    var result = new JObject
                    {
                        ["model"] = def.Model,
                        ["manufacturer"] = def.Manufacturer,
                        ["category"] = def.Category,
                        ["description"] = def.Description,
                        ["source"] = def.Source,
                        ["identity"] = def.Identity == null ? null : JObject.FromObject(def.Identity, Serializer()),
                        ["commandCount"] = def.Commands != null ? def.Commands.Count : 0
                    };

                    bool filtered = !string.IsNullOrEmpty(search) || !string.IsNullOrEmpty(category);
                    if (filtered && list.Count > 0 && list.Count <= 30)
                    {
                        result["commands"] = JArray.FromObject(list, Serializer());
                    }
                    else
                    {
                        var index = new JArray();
                        foreach (var c in list)
                            index.Add(new JObject
                            {
                                ["name"] = c.Name,
                                ["mnemonic"] = c.Mnemonic,
                                ["category"] = c.Category,
                                ["description"] = Brief(c.Description)
                            });
                        result["commandIndex"] = index;
                        result["note"] = "Compact index. Call instrument_reference with command=<name|mnemonic> " +
                                         "for full detail, or search=/category= to filter.";
                    }
                    return result.ToString(Formatting.Indented);
                }));

            // ---- Identify an instrument and match the DB ------------------------
            registry.Add(new McpTool(
                "instrument_identify",
                "Query an instrument's identity (tries the assigned model's ID command, then *IDN?, ID?) " +
                "and match the response against the database. NOTE: this sends a command to the instrument. " +
                "Pass read_bytes if a free-running instrument makes the identity read time out.",
                Schema(
                    Required("resource", "string", "VISA resource string, e.g. 'GPIB0::18::INSTR'."),
                    Prop("read_bytes", "integer", "Optional: read at most this many bytes (e.g. 512) so a free-running " +
                        "instrument's identity read returns promptly instead of timing out.")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    int readBytes = Int(args, "read_bytes", 0);
                    var io = InstrumentIo.Resolve(db, assignments, resource, VisaTimeoutMs, readBytes);
                    var attempts = new List<string>();
                    InstrumentDefinition assignedDef;
                    string assigned = assignments.Get(resource);
                    if (assigned != null && db.TryGet(assigned, out assignedDef) &&
                        assignedDef.Identity != null && !string.IsNullOrEmpty(assignedDef.Identity.Command))
                    {
                        attempts.Add(assignedDef.Identity.Command);
                    }
                    foreach (var c in new[] { "*IDN?", "ID?", "ID" })
                        if (!attempts.Contains(c)) attempts.Add(c);

                    string response = null, used = null, lastError = null;
                    foreach (var cmd in attempts)
                    {
                        try
                        {
                            response = visa.Query(resource, cmd, io);
                            used = cmd;
                            if (!string.IsNullOrWhiteSpace(response)) break;
                        }
                        catch (Exception ex) { lastError = ex.Message; }
                    }

                    if (string.IsNullOrWhiteSpace(response))
                        return "No identity response from " + resource +
                               (lastError != null ? " (last error: " + lastError + ")" : "") +
                               ". The instrument may not support an identification query; assign its model " +
                               "explicitly with assign_instrument.";

                    response = response.Trim();
                    var matches = db.MatchIdentity(response).Select(d => d.Model).ToList();
                    var sb = new StringBuilder();
                    sb.AppendLine("Identity (" + used + ") from " + resource + ": " + response);
                    sb.AppendLine(matches.Count == 0
                        ? "No database model matched this response."
                        : "Matched model(s): " + string.Join(", ", matches));
                    return sb.ToString().TrimEnd();
                }));

            // ---- Assign a model to a resource (persist on confirm) ---------------
            registry.Add(new McpTool(
                "assign_instrument",
                "Record which model sits at a VISA resource (e.g. an 8563E at GPIB0::18::INSTR). " +
                "Verifies identity by default. Does NOT save unless confirm=true - first call reports the " +
                "proposed assignment so you can confirm with the user, then call again with confirm=true.",
                Schema(
                    Required("resource", "string", "VISA resource string, e.g. 'GPIB0::18::INSTR'."),
                    Required("model", "string", "Model name or alias from the database."),
                    Prop("confirm", "boolean", "Set true to actually persist the assignment (default false)."),
                    Prop("verify", "boolean", "Send the model's identity command to confirm (default true).")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    string model = ReqStr(args, "model");
                    bool confirm = Bool(args, "confirm", false);
                    bool verify = Bool(args, "verify", true);

                    InstrumentDefinition def;
                    if (!db.TryGet(model, out def))
                        return "Unknown model '" + model + "'. See instrument_list_models, or add it with " +
                               "instrument_db_save.";

                    bool verifyOk;
                    string verifyNote = VerifyIdentity(visa, def, verify, resource, out verifyOk);

                    if (!confirm)
                    {
                        return "PROPOSED assignment (NOT yet saved):\n" +
                               "  resource: " + resource + "\n" +
                               "  model:    " + def.Model + "\n" +
                               "  " + verifyNote + "\n\n" +
                               "ASSISTANT: Confirm with the user, then call assign_instrument again with " +
                               "confirm=true to persist." +
                               (verifyOk ? "" : "\nNote: identity verification did not pass - double-check the " +
                                                "model/address before confirming.");
                    }

                    assignments.Set(resource, def.Model);
                    return "Saved assignment: " + resource + " -> " + def.Model + ". " + verifyNote;
                }));

            // ---- List current assignments ---------------------------------------
            registry.Add(new McpTool(
                "list_assignments",
                "List the VISA resource-to-model assignments currently recorded by the server.",
                Schema(),
                args =>
                {
                    var all = assignments.All();
                    if (all.Count == 0) return "No instruments are currently assigned.";
                    var sb = new StringBuilder();
                    sb.AppendLine(all.Count + " assignment(s):");
                    foreach (var kv in all.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        InstrumentDefinition def;
                        string suffix = db.TryGet(kv.Value, out def) && !string.IsNullOrEmpty(def.Category)
                            ? " (" + def.Category + ")"
                            : (db.TryGet(kv.Value, out def) ? "" : " (model not in database)");
                        sb.AppendLine("  " + kv.Key + " -> " + kv.Value + suffix);
                    }
                    return sb.ToString().TrimEnd();
                }));

            // ---- Remove an assignment (persist on confirm) ----------------------
            registry.Add(new McpTool(
                "unassign_instrument",
                "Remove a recorded resource-to-model assignment. Does NOT remove unless confirm=true.",
                Schema(
                    Required("resource", "string", "VISA resource string to unassign."),
                    Prop("confirm", "boolean", "Set true to actually remove the assignment (default false).")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    string current = assignments.Get(resource);
                    if (current == null) return "No assignment exists for " + resource + ".";
                    if (!Bool(args, "confirm", false))
                        return "Would remove assignment " + resource + " -> " + current +
                               ". Call again with confirm=true to remove.";
                    assignments.Remove(resource);
                    return "Removed assignment for " + resource + ".";
                }));

            // ---- Add/replace a definition (persist on confirm) ------------------
            registry.Add(new McpTool(
                "instrument_db_save",
                "Add or update an instrument definition in the user database (makes the model user-extensible). " +
                "Pass the full definition object. Does NOT write unless confirm=true.",
                Schema(
                    Required("definition", "object", "Instrument definition: { model, manufacturer, category, " +
                        "identity:{command,matchRegex}, commands:[{name,mnemonic,set,query,description}] }."),
                    Prop("confirm", "boolean", "Set true to actually write the file (default false).")),
                args =>
                {
                    var token = args["definition"] as JObject;
                    if (token == null) throw new ArgumentException("'definition' must be an object");

                    InstrumentDefinition def;
                    try { def = token.ToObject<InstrumentDefinition>(); }
                    catch (Exception ex) { throw new ArgumentException("invalid definition: " + ex.Message); }
                    if (def == null || string.IsNullOrWhiteSpace(def.Model))
                        throw new ArgumentException("definition.model is required");

                    string dir = InstrumentPaths.UserDatabaseDir();
                    string file = Path.Combine(dir, SanitizeFileName(def.Model) + ".json");
                    bool exists = File.Exists(file);
                    int cmds = def.Commands != null ? def.Commands.Count : 0;

                    if (!Bool(args, "confirm", false))
                        return "PROPOSED save of model '" + def.Model + "' (" + cmds + " commands) to:\n  " + file +
                               (exists ? "\n  (this will OVERWRITE the existing file)" : "") +
                               "\n\nASSISTANT: Confirm with the user, then call again with confirm=true to write.";

                    Directory.CreateDirectory(dir);
                    File.WriteAllText(file, JsonConvert.SerializeObject(def, JsonSettings));
                    db.Upsert(def); // make it usable immediately
                    return "Saved model '" + def.Model + "' (" + cmds + " commands) to " + file + ".";
                }));

            // ---- Reset a user copy back to the bundled (shipped) definition -----
            registry.Add(new McpTool(
                "instrument_db_refresh",
                "Reset a model's USER database copy back to the bundled (shipped) definition so bundled " +
                "fixes/updates take effect. Use when a shipped definition was corrected but your saved copy " +
                "still has the old values (new bundled BLOCKS are merged automatically on load; whole-block " +
                "value changes you already have are not - this pulls them in). Backs the user copy up to " +
                "<file>.bak and removes it (the bundled default is then used). Does NOT change anything " +
                "unless confirm=true.",
                Schema(
                    Required("model", "string", "Model name or alias to restore from the bundled defaults."),
                    Prop("confirm", "boolean", "Set true to actually reset the user copy (default false).")),
                args =>
                {
                    string model = ReqStr(args, "model");
                    string userDir = InstrumentPaths.UserDatabaseDir();

                    JObject bundled; string bundledFile;
                    if (!TryFindByModel(bundledDir, model, out bundled, out bundledFile))
                        return "No bundled definition matches '" + model + "'. instrument_db_refresh only " +
                               "restores shipped models; there is nothing to reset to.";
                    string canonical = (string)bundled.GetValue("model", StringComparison.OrdinalIgnoreCase);

                    JObject userObj; string userFile;
                    if (!TryFindByModel(userDir, model, out userObj, out userFile))
                        return "No user override exists for '" + canonical +
                               "' - the bundled definition is already in effect.";

                    if (!Bool(args, "confirm", false))
                        return "PROPOSED refresh of '" + canonical + "':\n" +
                               "  user copy:  " + userFile + "  (backed up to *.bak, then removed)\n" +
                               "  restored to: " + bundledFile + "\n\n" +
                               "ASSISTANT: Confirm with the user, then call again with confirm=true. Restart the " +
                               "server (Claude Desktop) afterwards so the change is fully reloaded.";

                    string backup = userFile + ".bak";
                    File.Copy(userFile, backup, overwrite: true);
                    File.Delete(userFile);

                    InstrumentDefinition def;
                    try { def = bundled.ToObject<InstrumentDefinition>(); }
                    catch (Exception ex) { return "Removed user copy but the bundled '" + canonical +
                        "' failed to parse: " + ex.Message + " (old copy at " + backup + ")."; }
                    db.Upsert(def); // make the bundled def effective immediately

                    return "Refreshed '" + canonical + "' to the bundled definition. Old user copy backed up to " +
                           backup + ". Restart the server to fully reload.";
                }));

            // ---- Set per-model I/O termination + bounded read (persist on confirm) ----
            registry.Add(new McpTool(
                "set_termination",
                "Configure how this server terminates writes to / reads from a MODEL, and an optional bounded " +
                "read length (max_read_bytes) for FREE-RUNNING instruments that stream output continuously and " +
                "never assert a normal end-of-response - so identity/queries return promptly instead of timing " +
                "out. Target the model by name, or by resource (its assigned model is used). These settings then " +
                "apply automatically to visa_query/visa_read/identify for that instrument. Persists to the user " +
                "database; does NOT write unless confirm=true.",
                Schema(
                    Prop("model", "string", "Model name or alias to configure. Provide either model or resource."),
                    Prop("resource", "string", "VISA resource whose assigned model to configure. Provide either model or resource."),
                    Prop("read_terminator", "string", "Read terminator: 'LF', 'CR', 'CRLF', 'none', or a literal like '\\n'. Omit to leave unchanged."),
                    Prop("write_terminator", "string", "Write terminator: 'LF', 'CR', 'CRLF', 'none', or a literal. Omit to leave unchanged."),
                    Prop("max_read_bytes", "integer", "Bounded read length for a free-running instrument (e.g. 512); 0 clears it. Omit to leave unchanged."),
                    Prop("confirm", "boolean", "Set true to actually persist (default false = propose only).")),
                args => SetTermination(db, assignments, args)));
        }

        // ------------------------------------------------------------------------
        // set_termination - configure per-model terminators + bounded read (#35).
        // Mirrors the confirm-to-save pattern of the other DB writers: proposes on the first call,
        // and on confirm=true writes a minimal user-DB override (model + termination + maxReadBytes)
        // that merges over the bundled blocks on load, then makes it effective immediately.
        // ------------------------------------------------------------------------
        private static string SetTermination(InstrumentDatabase db, AssignmentStore assignments, JObject args)
        {
            string model = Str(args, "model", null);
            string resource = Str(args, "resource", null);
            if (string.IsNullOrEmpty(model) && string.IsNullOrEmpty(resource))
                return "Provide either 'model' or 'resource'.";
            if (string.IsNullOrEmpty(model))
            {
                model = assignments.Get(resource);
                if (string.IsNullOrEmpty(model))
                    return "No model is assigned to " + resource +
                           ". Assign one with assign_instrument, or pass 'model' directly.";
            }

            InstrumentDefinition def;
            if (!db.TryGet(model, out def))
                return "Unknown model '" + model + "'. See instrument_list_models, or add it with instrument_db_save.";

            string read = def.Termination != null ? def.Termination.Read : null;
            string write = def.Termination != null ? def.Termination.Write : null;
            int? maxBytes = def.MaxReadBytes;

            bool changed = false;
            if (IsSupplied(args, "read_terminator"))
            {
                string parsed, err;
                if (!TryParseTerminator(Str(args, "read_terminator", null), out parsed, out err)) return err;
                read = parsed; changed = true;
            }
            if (IsSupplied(args, "write_terminator"))
            {
                string parsed, err;
                if (!TryParseTerminator(Str(args, "write_terminator", null), out parsed, out err)) return err;
                write = parsed; changed = true;
            }
            if (IsSupplied(args, "max_read_bytes"))
            {
                int n = Int(args, "max_read_bytes", 0);
                if (n < 0) return "max_read_bytes must be >= 0 (0 clears it).";
                maxBytes = n > 0 ? (int?)n : null; changed = true;
            }
            if (!changed)
                return "Nothing to change. Pass read_terminator, write_terminator, and/or max_read_bytes.";

            string dir = InstrumentPaths.UserDatabaseDir();
            string file = Path.Combine(dir, SanitizeFileName(model) + ".json");
            bool exists = File.Exists(file);

            string summary =
                "  read terminator:  " + DescribeTerminator(read) + "\n" +
                "  write terminator: " + DescribeTerminator(write) + "\n" +
                "  max read bytes:   " + (maxBytes.HasValue ? maxBytes.Value.ToString()
                                                            : "(unset - read to terminator/EOI)");

            if (!Bool(args, "confirm", false))
                return "PROPOSED I/O settings for '" + def.Model + "' (would " + (exists ? "update" : "create") +
                       " " + file + "):\n" + summary +
                       "\n\nASSISTANT: Confirm with the user, then call set_termination again with confirm=true to persist.";

            try
            {
                Directory.CreateDirectory(dir);
                JObject userJson = exists ? JObject.Parse(File.ReadAllText(file)) : new JObject();
                if (userJson["model"] == null) userJson["model"] = def.Model;
                var term = new JObject();
                if (read != null) term["read"] = read;
                if (write != null) term["write"] = write;
                if (term.HasValues) userJson["termination"] = term;
                if (maxBytes.HasValue) userJson["maxReadBytes"] = maxBytes.Value;
                else userJson.Remove("maxReadBytes");
                File.WriteAllText(file, userJson.ToString(Formatting.Indented));
            }
            catch (Exception ex) { return "Failed to save I/O settings for '" + def.Model + "': " + ex.Message; }

            // Effective for this session immediately.
            if (read != null || write != null)
                def.Termination = new TerminationSpec { Read = read, Write = write };
            def.MaxReadBytes = maxBytes;
            db.Upsert(def);

            return "Saved I/O settings for '" + def.Model + "' to " + file + ":\n" + summary;
        }

        private static bool IsSupplied(JObject args, string key)
        {
            var t = args[key];
            return t != null && t.Type != JTokenType.Null;
        }

        /// <summary>Renders a terminator for display: null = unchanged/none, "" = none, else the escaped literal.</summary>
        private static string DescribeTerminator(string term)
        {
            if (term == null) return "(none)";
            if (term.Length == 0) return "none";
            return "\"" + term.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        /// <summary>
        /// Parses a terminator argument: the mnemonics LF/CR/CRLF/NONE (case-insensitive), or a literal
        /// string (e.g. a real newline passed through JSON). "none"/empty yields "" (no terminator).
        /// </summary>
        private static bool TryParseTerminator(string raw, out string value, out string error)
        {
            value = null; error = null;
            if (raw == null) { error = "terminator value missing"; return false; }
            switch (raw.Trim().ToUpperInvariant())
            {
                case "": case "NONE": value = ""; return true;
                case "LF": case "\\N": value = "\n"; return true;
                case "CR": case "\\R": value = "\r"; return true;
                case "CRLF": case "CR/LF": case "CR LF": case "\\R\\N": value = "\r\n"; return true;
            }
            value = raw; // literal terminator as given
            return true;
        }

        // ------------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------------

        private static string VerifyIdentity(IInstrumentManager visa, InstrumentDefinition def, bool verify,
                                             string resource, out bool ok)
        {
            ok = true;
            if (!verify) return "Verification skipped.";
            if (def.Identity == null || string.IsNullOrEmpty(def.Identity.Command))
                return "No identity command defined for " + def.Model + "; cannot verify automatically.";

            try
            {
                var io = InstrumentIo.FromDefinition(def, VisaTimeoutMs);
                string resp = (visa.Query(resource, def.Identity.Command, io) ?? string.Empty).Trim();
                bool matched = string.IsNullOrEmpty(def.Identity.MatchRegex) ||
                               Regex.IsMatch(resp, def.Identity.MatchRegex, RegexOptions.IgnoreCase);
                ok = matched;
                return "Identity query " + def.Identity.Command + " returned \"" + resp + "\" -> " +
                       (matched ? "matches " + def.Model
                                : "does NOT match expected pattern /" + def.Identity.MatchRegex + "/");
            }
            catch (Exception ex)
            {
                ok = false;
                return "Identity query " + def.Identity.Command + " failed: " + ex.Message;
            }
        }

        /// <summary>Finds the *.json in <paramref name="dir"/> whose model name or alias matches.</summary>
        private static bool TryFindByModel(string dir, string model, out JObject jo, out string file)
        {
            jo = null; file = null;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                JObject o;
                try { o = JObject.Parse(File.ReadAllText(f)); } catch { continue; }
                string m = (string)o.GetValue("model", StringComparison.OrdinalIgnoreCase);
                bool match = string.Equals(m, model, StringComparison.OrdinalIgnoreCase);
                if (!match && o.GetValue("aliases", StringComparison.OrdinalIgnoreCase) is JArray aliases)
                    match = aliases.Any(a => string.Equals((string)a, model, StringComparison.OrdinalIgnoreCase));
                if (match) { jo = o; file = f; return true; }
            }
            return false;
        }

        private static InstrumentCommand FindCommand(InstrumentDefinition def, string key)
        {
            if (def.Commands == null) return null;
            return def.Commands.FirstOrDefault(c =>
                string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Mnemonic, key, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ContainsCI(string haystack, string needle) =>
            haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Brief(string description)
        {
            if (string.IsNullOrEmpty(description)) return null;
            string s = description.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 140 ? s : s.Substring(0, 137) + "...";
        }

        private static JsonSerializer Serializer() => JsonSerializer.Create(JsonSettings);

        private static string SanitizeFileName(string model)
        {
            var sb = new StringBuilder(model.Length);
            foreach (char c in model)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
