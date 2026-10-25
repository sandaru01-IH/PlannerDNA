using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PlannerDNA.Storage
{
    public readonly struct StoreResult
    {
        public readonly bool Saved;
        public readonly bool Duplicate;
        public readonly string Message;
        public readonly string Path;

        public StoreResult(bool saved, bool duplicate, string message, string path)
        {
            Saved = saved;
            Duplicate = duplicate;
            Message = message;
            Path = path;
        }
    }

    /// <summary>
    /// Durable on-disk JSON storage; works with no backend at all. Layout under the root:
    ///   alternatives/&lt;alternative_id&gt;.json   saved A/B snapshots
    ///   decisions/&lt;comparison_id&gt;.json      explicit A/B/tie/skip choices (one file each, never overwritten)
    ///   saves/&lt;scenario_id&gt;.json, saves/latest.json   design save slots
    ///   events/&lt;session_id&gt;.jsonl           append-only edit/undo/redo log
    /// </summary>
    public sealed class LocalDecisionStore
    {
        public LocalDecisionStore(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory)) throw new ArgumentException("Root directory is required.");
            RootDirectory = rootDirectory;
        }

        public static string DefaultRoot => Path.Combine(Application.persistentDataPath, "PlannerDNA");

        public string RootDirectory { get; }
        public string AlternativesDir => Path.Combine(RootDirectory, "alternatives");
        public string DecisionsDir => Path.Combine(RootDirectory, "decisions");
        public string SavesDir => Path.Combine(RootDirectory, "saves");
        public string EventsDir => Path.Combine(RootDirectory, "events");
        public string ExportsDir => Path.Combine(RootDirectory, "exports");
        public string OutboxDir => Path.Combine(RootDirectory, "outbox");
        /// <summary>Backups live beside (not inside) the data folder so "delete all data" never removes them.</summary>
        public string BackupsDir => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(RootDirectory)) ?? RootDirectory,
            Path.GetFileName(Path.GetFullPath(RootDirectory)) + "_backups");

        private string[] DataDirectories => new[] { AlternativesDir, DecisionsDir, SavesDir, EventsDir, OutboxDir, ExportsDir };

        private static void RequireSafeId(string id, string what)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException($"{what} is required.");
            foreach (char ch in id)
            {
                bool ok = char.IsLetterOrDigit(ch) || ch == '-' || ch == '_';
                if (!ok) throw new ArgumentException($"{what} '{id}' contains characters not allowed in a file name.");
            }
        }

        // ---- Alternatives ----

        public string SaveAlternative(AlternativeDto alt)
        {
            RequireSafeId(alt.alternative_id, "alternative_id");
            string path = Path.Combine(AlternativesDir, alt.alternative_id + ".json");
            AtomicFile.WriteAllText(path, JsonUtility.ToJson(alt, true));
            return path;
        }

        public AlternativeDto LoadAlternative(string alternativeId)
        {
            if (string.IsNullOrEmpty(alternativeId)) return null;
            RequireSafeId(alternativeId, "alternative_id");
            string path = Path.Combine(AlternativesDir, alternativeId + ".json");
            return File.Exists(path) ? JsonUtility.FromJson<AlternativeDto>(File.ReadAllText(path)) : null;
        }

        // ---- Decisions ----

        public StoreResult SavePreference(PreferenceDto pref)
        {
            RequireSafeId(pref.comparison_id, "comparison_id");
            if (pref.alternative_a_id == pref.alternative_b_id)
                return new StoreResult(false, false, "An alternative cannot be compared with itself.", null);
            if (pref.choice != "A" && pref.choice != "B" && pref.choice != "tie" && pref.choice != "skip")
                return new StoreResult(false, false, $"Invalid choice '{pref.choice}'.", null);

            string path = Path.Combine(DecisionsDir, pref.comparison_id + ".json");
            if (File.Exists(path))
                return new StoreResult(false, true, "This comparison was already recorded; the original record is kept.", path);

            foreach (PreferenceDto existing in LoadAllPreferences())
            {
                bool samePair = (existing.alternative_a_id == pref.alternative_a_id && existing.alternative_b_id == pref.alternative_b_id)
                    || (existing.alternative_a_id == pref.alternative_b_id && existing.alternative_b_id == pref.alternative_a_id);
                if (samePair && IsDistinctEvaluationTrial(existing, pref)) continue;
                if (samePair)
                    return new StoreResult(false, true,
                        $"This A/B pair already has a recorded decision ({existing.comparison_id}). Save a new A or B to compare again.",
                        Path.Combine(DecisionsDir, existing.comparison_id + ".json"));
            }

            AtomicFile.WriteAllText(path, JsonUtility.ToJson(pref, true));
            return new StoreResult(true, false, "Decision saved locally.", path);
        }

        /// <summary>A blinded evaluation deliberately shows some pairs twice (sides swapped), each as its own trial.</summary>
        private static bool IsDistinctEvaluationTrial(PreferenceDto existing, PreferenceDto pref)
        {
            return existing.mode == "evaluation" && pref.mode == "evaluation"
                && !string.IsNullOrEmpty(existing.evaluation_trial_id) && !string.IsNullOrEmpty(pref.evaluation_trial_id)
                && existing.evaluation_trial_id != pref.evaluation_trial_id;
        }

        public List<PreferenceDto> LoadAllPreferences()
        {
            var list = new List<PreferenceDto>();
            if (!Directory.Exists(DecisionsDir)) return list;
            string[] files = Directory.GetFiles(DecisionsDir, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                PreferenceDto dto = JsonUtility.FromJson<PreferenceDto>(File.ReadAllText(file));
                if (dto != null && !string.IsNullOrEmpty(dto.comparison_id)) list.Add(dto);
            }
            return list;
        }

        public PreferenceStats ComputeStats()
        {
            var stats = new PreferenceStats();
            var scenarios = new HashSet<string>();
            foreach (PreferenceDto p in LoadAllPreferences())
            {
                stats.unique_comparisons++;
                scenarios.Add(p.scenario_id);
                switch (p.choice)
                {
                    case "A": stats.choice_a++; break;
                    case "B": stats.choice_b++; break;
                    case "tie": stats.ties++; break;
                    case "skip": stats.skips++; break;
                }
                if (p.training_eligible) stats.training_eligible++;
            }
            stats.scenarios = scenarios.Count;
            return stats;
        }

        // ---- Design saves ----

        public string SaveGame(SaveGameDto save)
        {
            RequireSafeId(save.scenario_id, "scenario_id");
            string json = JsonUtility.ToJson(save, true);
            string path = Path.Combine(SavesDir, save.scenario_id + ".json");
            AtomicFile.WriteAllText(path, json);
            AtomicFile.WriteAllText(Path.Combine(SavesDir, "latest.json"), json);
            return path;
        }

        public SaveGameDto LoadGame(string scenarioId)
        {
            RequireSafeId(scenarioId, "scenario_id");
            return ReadSave(Path.Combine(SavesDir, scenarioId + ".json"));
        }

        public SaveGameDto LoadLatestGame() => ReadSave(Path.Combine(SavesDir, "latest.json"));

        public bool HasLatestGame => File.Exists(Path.Combine(SavesDir, "latest.json"));

        private static SaveGameDto ReadSave(string path)
        {
            return File.Exists(path) ? JsonUtility.FromJson<SaveGameDto>(File.ReadAllText(path)) : null;
        }

        // ---- Event log ----

        public void AppendEvent(TimelineEntryDto entry)
        {
            RequireSafeId(entry.session_id, "session_id");
            AtomicFile.AppendLine(Path.Combine(EventsDir, entry.session_id + ".jsonl"), JsonUtility.ToJson(entry));
        }

        public List<TimelineEntryDto> LoadEvents(string sessionId)
        {
            RequireSafeId(sessionId, "session_id");
            return ReadEventFile(Path.Combine(EventsDir, sessionId + ".jsonl"));
        }

        private static List<TimelineEntryDto> ReadEventFile(string path)
        {
            var list = new List<TimelineEntryDto>();
            if (!File.Exists(path)) return list;
            foreach (string line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                list.Add(JsonUtility.FromJson<TimelineEntryDto>(line));
            }
            return list;
        }

        // ---- Export and deletion ----

        private static string[] SortedFiles(string dir, string pattern)
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            string[] files = Directory.GetFiles(dir, pattern);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }

        /// <summary>Collects every local record into one JSON document (works without the backend).</summary>
        public LocalExportDto BuildExport(DateTime nowUtc)
        {
            var export = new LocalExportDto
            {
                schema_version = Core.Versions.SchemaVersion,
                app_version = Core.Versions.AppVersion,
                generator_version = Core.Versions.GeneratorVersion,
                metric_version = Core.Versions.MetricVersion,
                cost_rules_version = Core.Versions.CostRulesVersion,
                feature_version = Core.Versions.FeatureVersion,
                exported_utc = nowUtc.ToString("o"),
                note = "Synthetic planning data created on this computer. Each alternative's snapshot reproduces its city exactly.",
            };
            foreach (string file in SortedFiles(AlternativesDir, "*.json"))
                export.alternatives.Add(JsonUtility.FromJson<AlternativeDto>(File.ReadAllText(file)));
            export.preferences.AddRange(LoadAllPreferences());
            foreach (string file in SortedFiles(EventsDir, "*.jsonl"))
                export.events.AddRange(ReadEventFile(file));
            foreach (string file in SortedFiles(SavesDir, "*.json"))
                if (Path.GetFileName(file) != "latest.json")
                    export.saves.Add(JsonUtility.FromJson<SaveGameDto>(File.ReadAllText(file)));
            return export;
        }

        public string WriteExport(DateTime nowUtc)
        {
            string path = Path.Combine(ExportsDir, $"plannerdna-local-export-{nowUtc:yyyyMMdd'T'HHmmss'Z'}.json");
            AtomicFile.WriteAllText(path, JsonUtility.ToJson(BuildExport(nowUtc), true));
            return path;
        }

        /// <summary>Copies every data folder to a timestamped backup folder outside the data folder.</summary>
        public string Backup(DateTime nowUtc)
        {
            string target = Path.Combine(BackupsDir, $"backup-{nowUtc:yyyyMMdd'T'HHmmssfff'Z'}");
            Directory.CreateDirectory(target);
            foreach (string dir in DataDirectories)
                if (Directory.Exists(dir)) CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            return target;
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (string dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        /// <summary>Permanently removes all local PlannerDNA records. Backups are kept.</summary>
        public void DeleteAllData()
        {
            foreach (string dir in DataDirectories)
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        public bool HasAnyData()
        {
            foreach (string dir in DataDirectories)
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length > 0) return true;
            return false;
        }
    }
}
