using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GpibMcp.Manuals;
using GpibMcp.Mcp;
using Newtonsoft.Json.Linq;
using static GpibMcp.Tools.ToolArgs;

namespace GpibMcp.Tools
{
    /// <summary>
    /// Searching the user's own folder of instrument manuals (#120).
    ///
    /// The command database answers first and answers fast. This is what to do when it cannot: the manual a
    /// database entry was derived from is often sitting on the same disk, and reading it is far better than
    /// guessing a command at an instrument. The tool returns the passages and cites them; deciding what they
    /// mean is the model's job, in front of the user, who can read the quote.
    ///
    /// Registered only when a library is configured, so a server without one carries no tool that could
    /// promise something it cannot do.
    /// </summary>
    public static class ManualTools
    {
        public static void Register(ToolRegistry registry, ManualLibrary library)
        {
            if (registry == null || library == null) return;

            registry.Add(new McpTool(
                "manual_search",
                "Search the user's local folder of instrument manuals and return the matching passages, with " +
                "the file and page they came from. Use this when instrument_reference does NOT have the " +
                "command you need - a model that isn't in the database, a command that isn't listed, or a " +
                "detail (bit meanings, a status byte, a plot command's arguments) the entry doesn't cover. " +
                "ALWAYS try instrument_reference first: it is instant and already structured. " +
                "ALWAYS pass 'model' when you know it - the library can be hundreds of manuals and the model " +
                "is what narrows it to the right ones. " +
                "This returns SOURCE TEXT, not an answer: read the passage, tell the user what it says, and " +
                "CITE the file and page. If it gives you a command the database lacks, offer to add it with " +
                "instrument_db_save so the next lookup is instant. Do NOT send a command to an instrument " +
                "purely on your own reading of a passage without telling the user where it came from.",
                Schema(
                    Required("query", "string", "What to look for, e.g. 'center frequency command' or " +
                        "'status byte bit 4' or 'OUTPPLOT'. Include the mnemonic if you know it."),
                    Prop("model", "string", "Model whose manuals to search, e.g. '8563E'. Strongly preferred: " +
                        "without it every manual in the library is a candidate and the search is capped."),
                    Prop("file", "string", "A specific manual to search, as returned in an earlier result " +
                        "(path relative to the library root). Use to read more of a file that already matched."),
                    Prop("max_results", "integer", "Passages to return (default 5, max 20)."),
                    Prop("context_chars", "integer", "Characters of context per passage (default 400, max 4000).")),
                (Func<JObject, ToolCallContext, ToolOutput>)((args, ctx) => Search(args, ctx, library)))
                .WithOutputSchema(OutputSchema));
        }

        private static ToolOutput Search(JObject args, ToolCallContext ctx, ManualLibrary library)
        {
            string query = ReqStr(args, "query");
            string model = Str(args, "model", null);
            string file = Str(args, "file", null);
            int maxResults = Math.Min(Math.Max(Int(args, "max_results", 5), 1), 20);
            int contextChars = Math.Min(Math.Max(Int(args, "context_chars", ManualSearch.DefaultContextChars), 100), 4000);

            IReadOnlyList<ManualFile> candidates;
            if (!string.IsNullOrWhiteSpace(file))
            {
                ManualFile one = library.Resolve(file);
                if (one == null)
                    return Failed("No manual at '" + file + "' inside the library (" + library.Root + ").");
                candidates = new[] { one };
            }
            else
            {
                candidates = library.Candidates(model, query);
            }

            if (candidates.Count == 0) return NothingMatched(library, query, model);

            ctx.Progress(1, candidates.Count + 1, "Searching " + candidates.Count + " manual(s).");
            ManualSearch.Results results = ManualSearch.Run(candidates, query, maxResults, contextChars,
                (index, total, name) => ctx.Progress(index + 1, total + 1, "Reading " + name + "."));
            ctx.Progress(candidates.Count + 1, candidates.Count + 1, "Search complete.");

            // Nothing named for the instrument itself was read, only its series - a substitution, and one
            // the user has to be told about. Judged on whether ANY candidate named the model, so a single
            // unrelated file cannot silence the caveat.
            bool familyOnly = !string.IsNullOrEmpty(model) &&
                              !candidates.Any(c => ManualLibrary.MatchesModel(c, model)) &&
                              candidates.Any(c => ManualLibrary.IsFamilyMatchOnly(c, model));
            return Deliver(library, query, model, results, familyOnly);
        }

        /// <summary>
        /// No manual's name matched. Saying "searched 12 files, nothing found" after reading whichever files
        /// happened to be smallest would be worse than useless - it reads as "your library does not have
        /// this". Say what actually happened, and show what is there so the next call can aim.
        /// </summary>
        private static ToolOutput NothingMatched(ManualLibrary library, string query, string model)
        {
            IReadOnlyList<string> sample = library.SampleNames(model);
            int total = library.Count();

            var structured = new JObject
            {
                ["ok"] = true,
                ["query"] = query,
                ["library"] = library.Root,
                ["hits"] = new JArray(),
                ["searched"] = new JArray(),
                ["unreadable"] = new JArray(),
                ["available"] = new JArray(sample.Cast<object>().ToArray())
            };
            if (!string.IsNullOrEmpty(model)) structured["model"] = model;

            var text = new StringBuilder();
            text.AppendLine("No manual in " + library.Root + " is named for " +
                            (string.IsNullOrEmpty(model) ? "this query" : "'" + model + "'") +
                            ", so nothing was searched (" + total + " manual(s) in the library).");
            if (sample.Count > 0)
            {
                text.AppendLine();
                text.AppendLine(string.IsNullOrEmpty(model)
                    ? "Some of what is there:"
                    : "Closest by name - if one of these covers " + model + ", call again with file=<path>:");
                foreach (string name in sample) text.AppendLine("  " + name);
            }
            text.AppendLine();
            text.AppendLine("Tell the user the manual does not appear to be in their library rather than " +
                            "guessing the command - or ask which of the above to read.");

            return ToolOutput.Text(text.ToString().TrimEnd()).WithStructured(structured);
        }

        private static ToolOutput Deliver(ManualLibrary library, string query, string model,
                                          ManualSearch.Results results, bool familyOnly)
        {
            var structured = new JObject
            {
                ["ok"] = true,
                ["query"] = query,
                ["library"] = library.Root,
                ["searched"] = new JArray(results.Searched.Cast<object>().ToArray()),
                ["hits"] = new JArray(results.Hits.Select(h => (JToken)new JObject
                {
                    ["file"] = h.File,
                    ["page"] = h.Page,
                    ["text"] = h.Text
                })),
                ["unreadable"] = new JArray(results.Unreadable.Select(u => (JToken)new JObject
                {
                    ["file"] = u.File,
                    ["problem"] = u.Problem
                }))
            };
            if (!string.IsNullOrEmpty(model)) structured["model"] = model;
            if (familyOnly) structured["familyMatchOnly"] = true;

            var text = new StringBuilder();
            if (familyOnly)
            {
                // The user asked about one instrument and got its series' manual. Usually right - families
                // share a command set - but it is a substitution, and substitutions get said out loud.
                text.AppendLine("NOTE: no manual is named for " + model + " exactly; these are from the same " +
                                "series. Command sets usually match across a series, but say so when you " +
                                "quote this, and check the passage really covers " + model + ".");
                text.AppendLine();
            }

            if (results.Hits.Count == 0)
            {
                text.AppendLine("No passage matched \"" + query + "\" in " + results.Searched.Count +
                                " manual(s) searched under " + library.Root + ".");
                if (string.IsNullOrEmpty(model))
                    text.AppendLine("Searching without a model is a wide net - pass model= to narrow it to " +
                                    "that instrument's manuals.");
            }
            else
            {
                text.AppendLine(results.Hits.Count + " passage(s) for \"" + query + "\"" +
                                (string.IsNullOrEmpty(model) ? "" : " (" + model + ")") + ":");
                foreach (ManualSearch.Hit hit in results.Hits)
                {
                    text.AppendLine();
                    text.AppendLine("--- " + hit.File + ", page " + hit.Page + " ---");
                    text.AppendLine(hit.Text);
                }
                text.AppendLine();
                text.AppendLine("CITE the file and page when you tell the user what this says. If it gives a " +
                                "command the database lacks, offer to add it with instrument_db_save.");
            }

            // Never let an unreadable manual look like a manual with no match.
            if (results.Unreadable.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Could not read " + results.Unreadable.Count + " file(s):");
                foreach (ManualSearch.FileNote note in results.Unreadable.Take(5))
                    text.AppendLine("  " + note.File + " - " + note.Problem);
                text.AppendLine("Tell the user this: the answer may be in a manual that could not be searched.");
            }

            return ToolOutput.Text(text.ToString().TrimEnd()).WithStructured(structured);
        }

        private static ToolOutput Failed(string message) =>
            ToolOutput.Text(message)
                      .WithStructured(new JObject { ["ok"] = false, ["error"] = message })
                      .AsError();

        private static JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["description"] = "Passages found in the user's manual library, each citing its file and page.",
            ["properties"] = new JObject
            {
                ["ok"] = new JObject { ["type"] = "boolean" },
                ["query"] = new JObject { ["type"] = "string" },
                ["model"] = new JObject { ["type"] = "string" },
                ["library"] = new JObject { ["type"] = "string", ["description"] = "Root folder that was searched." },
                ["hits"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "Matching passages, best first. Source text to read and cite - not an answer.",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["file"] = new JObject { ["type"] = "string", ["description"] = "Path within the library." },
                            ["page"] = new JObject { ["type"] = "integer", ["description"] = "1-based page, for the citation." },
                            ["text"] = new JObject { ["type"] = "string" }
                        },
                        ["required"] = new JArray("file", "page", "text")
                    }
                },
                ["searched"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "Files actually read.",
                    ["items"] = new JObject { ["type"] = "string" }
                },
                ["available"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "Present only when no manual matched: names in the library to aim a retry at.",
                    ["items"] = new JObject { ["type"] = "string" }
                },
                ["familyMatchOnly"] = new JObject
                {
                    ["type"] = "boolean",
                    ["description"] = "True when nothing was named for this model exactly and its series' " +
                                      "manuals were read instead - a substitution the user must be told about."
                },
                ["unreadable"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "Files that could not be extracted, and why. A match may be hiding in one of these.",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["file"] = new JObject { ["type"] = "string" },
                            ["problem"] = new JObject { ["type"] = "string" }
                        }
                    }
                },
                ["error"] = new JObject { ["type"] = "string" }
            },
            ["required"] = new JArray("ok"),
            ["additionalProperties"] = false
        };
    }
}
