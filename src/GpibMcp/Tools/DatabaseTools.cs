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
                                    AssignmentStore assignments, IInstrumentManager visa)
        {
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
                "and match the response against the database. NOTE: this sends a command to the instrument.",
                Schema(
                    Required("resource", "string", "VISA resource string, e.g. 'GPIB0::18::INSTR'.")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
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
                            response = visa.Query(resource, cmd, VisaTimeoutMs);
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
                string resp = (visa.Query(resource, def.Identity.Command, VisaTimeoutMs) ?? string.Empty).Trim();
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
