using System;
using System.Collections.Generic;
using Ivi.Visa;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Decodes a VISA exception to a human-readable status name + meaning. These are the codes that
    /// actually diagnose bench problems - timeout (instrument off / wrong address / no response),
    /// no listeners, resource not found / busy, and so on.
    /// </summary>
    internal static class VisaErrorInfo
    {
        /// <summary>A decoded VISA status: a short name and a plain-English meaning.</summary>
        public sealed class Info
        {
            public string Name { get; }
            public string Meaning { get; }
            public bool HasName => !string.IsNullOrEmpty(Name);
            public Info(string name, string meaning) { Name = name; Meaning = meaning; }
        }

        private static readonly Info None = new Info(null, null);

        /// <summary>Decodes <paramref name="ex"/>; returns a nameless <see cref="Info"/> if it is not a VISA error.</summary>
        public static Info Describe(Exception ex)
        {
            // IOTimeoutException is a sibling of NativeVisaException (no ErrorCode) - map it directly.
            if (ex is IOTimeoutException) return Codes[Tmo];

            var native = ex as NativeVisaException;
            if (native != null) return DescribeCode(native.ErrorCode);

            return None;
        }

        /// <summary>Decodes a raw VISA status code to a name + meaning (unknown codes get a hex name).</summary>
        public static Info DescribeCode(int code)
        {
            Info info;
            if (Codes.TryGetValue(code, out info)) return info;
            return new Info("VISA 0x" + code.ToString("X8"),
                "VISA returned status 0x" + code.ToString("X8") + " (see the NI-VISA status-code reference).");
        }

        // Common VISA status codes, written as the signed int form of 0xBFFF00xx.
        private const int Tmo = unchecked((int)0xBFFF0015);

        private static readonly Dictionary<int, Info> Codes = new Dictionary<int, Info>
        {
            [Tmo] = new Info("VI_ERROR_TMO",
                "Timeout - the instrument did not respond in time. Check it is powered on, set to this address, and not busy."),
            [unchecked((int)0xBFFF005F)] = new Info("VI_ERROR_NLISTENERS",
                "No listener on the bus - nothing acknowledged at this address. Check the GPIB address, power, and cabling."),
            [unchecked((int)0xBFFF0011)] = new Info("VI_ERROR_RSRC_NFOUND",
                "Resource not found - no instrument or board matches this resource string."),
            [unchecked((int)0xBFFF000E)] = new Info("VI_ERROR_INV_RSRC_NAME",
                "Invalid resource name - the VISA resource string is malformed."),
            [unchecked((int)0xBFFF0072)] = new Info("VI_ERROR_RSRC_BUSY",
                "Resource busy - the instrument or session is currently in use."),
            [unchecked((int)0xBFFF0023)] = new Info("VI_ERROR_ABORT",
                "The operation was aborted."),
            [unchecked((int)0xBFFF003E)] = new Info("VI_ERROR_IO",
                "General I/O error on the bus."),
            [unchecked((int)0xBFFF00A6)] = new Info("VI_ERROR_CONN_LOST",
                "Connection to the instrument was lost."),
            [unchecked((int)0xBFFF0036)] = new Info("VI_ERROR_NCIC",
                "The controller is not Controller-In-Charge of the GPIB bus."),
            [unchecked((int)0xBFFF000A)] = new Info("VI_ERROR_INV_OBJECT",
                "Invalid or already-closed session/object."),
            [unchecked((int)0xBFFF0067)] = new Info("VI_ERROR_NSUP_OPER",
                "Operation not supported by this resource."),
            [unchecked((int)0xBFFF001D)] = new Info("VI_ERROR_INV_SETUP",
                "Invalid setup - the session attributes are inconsistent for this operation."),
        };
    }
}
