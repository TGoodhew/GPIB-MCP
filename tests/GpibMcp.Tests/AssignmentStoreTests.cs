using System.IO;
using GpibMcp.Instruments;
using Xunit;

namespace GpibMcp.Tests
{
    public class AssignmentStoreTests
    {
        [Fact]
        public void InMemory_SetGetRemove()
        {
            var store = AssignmentStore.InMemory();
            Assert.Null(store.Get("GPIB0::18::INSTR"));

            store.Set("GPIB0::18::INSTR", "8563E");
            Assert.Equal("8563E", store.Get("GPIB0::18::INSTR"));
            Assert.Single(store.All());

            Assert.True(store.Remove("GPIB0::18::INSTR"));
            Assert.Null(store.Get("GPIB0::18::INSTR"));
            Assert.False(store.Remove("GPIB0::18::INSTR"));
        }

        [Fact]
        public void FromFile_PersistsAcrossReload()
        {
            string path = Path.Combine(Path.GetTempPath(), "gpib_bind_" + Path.GetRandomFileName() + ".json");
            try
            {
                var store = AssignmentStore.FromFile(path);
                store.Set("GPIB0::18::INSTR", "8563E");
                store.Set("GPIB0::7::INSTR", "3325A");

                // A fresh store over the same file must see the saved assignments.
                var reloaded = AssignmentStore.FromFile(path);
                Assert.Equal("8563E", reloaded.Get("GPIB0::18::INSTR"));
                Assert.Equal("3325A", reloaded.Get("GPIB0::7::INSTR"));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
