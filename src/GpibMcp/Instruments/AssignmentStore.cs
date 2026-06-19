using System;
using System.Collections.Generic;
using System.IO;
using GpibMcp.Diagnostics;
using Newtonsoft.Json;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Persistent map of VISA resource string to instrument model ("GPIB0::18::INSTR" -> "8563E").
    /// Backed by a JSON file; mutations are written through immediately. Use
    /// <see cref="InMemory"/> for tests (no file I/O).
    /// </summary>
    public sealed class AssignmentStore
    {
        private readonly string _path; // null => in-memory only
        private readonly object _gate = new object();
        private Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private AssignmentStore(string path) { _path = path; }

        public static AssignmentStore InMemory() => new AssignmentStore(null);

        public static AssignmentStore FromFile(string path)
        {
            var store = new AssignmentStore(path);
            store.Load();
            return store;
        }

        public string PersistencePath => _path;

        public string Get(string resource)
        {
            if (resource == null) return null;
            lock (_gate)
            {
                string model;
                return _map.TryGetValue(resource, out model) ? model : null;
            }
        }

        public IDictionary<string, string> All()
        {
            lock (_gate) { return new Dictionary<string, string>(_map, StringComparer.OrdinalIgnoreCase); }
        }

        public void Set(string resource, string model)
        {
            lock (_gate) { _map[resource] = model; Save(); }
        }

        public bool Remove(string resource)
        {
            lock (_gate)
            {
                bool removed = _map.Remove(resource);
                if (removed) Save();
                return removed;
            }
        }

        private void Load()
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return;
            try
            {
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(_path));
                if (loaded != null)
                    _map = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
                Log.Info("Loaded " + _map.Count + " instrument assignment(s) from " + _path);
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to load assignments from '" + _path + "': " + ex.Message);
            }
        }

        private void Save()
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonConvert.SerializeObject(_map, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to save assignments to '" + _path + "': " + ex.Message);
            }
        }
    }
}
