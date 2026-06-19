using System;
using System.IO;
using GpibMcp.Diagnostics;
using Xunit;

namespace GpibMcp.Tests
{
    public class LogTests
    {
        [Fact]
        public void Write_SuppressesMessagesBelowMinimumLevel()
        {
            var originalLevel = Log.MinimumLevel;
            var originalError = Console.Error;
            try
            {
                var captured = new StringWriter();
                Console.SetError(captured);
                Log.MinimumLevel = LogLevel.Warn;

                Log.Info("should-be-hidden");
                Log.Error("should-be-shown");

                var output = captured.ToString();
                Assert.DoesNotContain("should-be-hidden", output);
                Assert.Contains("should-be-shown", output);
                Assert.Contains("ERROR", output);
            }
            finally
            {
                Console.SetError(originalError);
                Log.MinimumLevel = originalLevel;
            }
        }

        [Fact]
        public void Write_GoesToStandardError_NotStandardOut()
        {
            var originalLevel = Log.MinimumLevel;
            var originalOut = Console.Out;
            var originalError = Console.Error;
            try
            {
                var stdout = new StringWriter();
                var stderr = new StringWriter();
                Console.SetOut(stdout);
                Console.SetError(stderr);
                Log.MinimumLevel = LogLevel.Debug;

                Log.Info("hello");

                Assert.Equal(string.Empty, stdout.ToString());
                Assert.Contains("hello", stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                Log.MinimumLevel = originalLevel;
            }
        }

        [Theory]
        [InlineData(LogLevel.Error, LogLevel.Error, true)]
        [InlineData(LogLevel.Error, LogLevel.Info, false)]
        [InlineData(LogLevel.Info, LogLevel.Debug, false)]
        [InlineData(LogLevel.Debug, LogLevel.Debug, true)]
        [InlineData(LogLevel.Info, LogLevel.Warn, true)]
        public void IsEnabled_GatesByConfiguredLevel(LogLevel minimum, LogLevel candidate, bool expected)
        {
            var original = Log.MinimumLevel;
            try
            {
                Log.MinimumLevel = minimum;
                Assert.Equal(expected, Log.IsEnabled(candidate));
            }
            finally
            {
                Log.MinimumLevel = original;
            }
        }
    }
}
