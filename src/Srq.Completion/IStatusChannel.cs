namespace Srq.Completion
{
    /// <summary>
    /// The minimal instrument I/O the completion waiter needs: send a command, and serial-poll the
    /// status byte. Abstracted so the waiter has no dependency on VISA or the MCP layer, and can be
    /// driven headlessly by a simulated instrument for testing (no hardware).
    /// </summary>
    public interface IStatusChannel
    {
        /// <summary>Writes one or more commands to the instrument (e.g. the enable mask, the arm string).</summary>
        void Send(string command);

        /// <summary>Serial-polls and returns the status byte (0-255). On most instruments this clears the latched bits.</summary>
        int SerialPoll();
    }

    /// <summary>
    /// Optional, for a channel to an instrument whose error queue can be read: lets the waiter clear an
    /// error that was already queued before the operation, so an error at completion is the operation's.
    /// </summary>
    public interface IErrorQueueChannel
    {
        /// <summary>
        /// Reads the error queue until it is empty (and clears any status the errors latched), returning the
        /// errors read - none if it was already empty. Throws if the queue could not be emptied.
        /// </summary>
        System.Collections.Generic.IReadOnlyList<string> ClearErrors();
    }
}
