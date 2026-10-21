using System;

namespace HawChow.GTFOCombatPanel;

// Which result screen the settlement archive has already been written for.
//
// A page pointer alone is not an identity. The game keeps one result page alive for the whole session, so
// the second expedition of a session raises the *same* page object a second time, and a latch keyed on the
// pointer refused that second screen as a duplicate: a session with two failed runs wrote one file, and the
// second run's board was never archived even though it was measured and drawn. The run counter is what
// really separates two result screens on one page - it moves when a new expedition starts - so the pair is
// the key, and two passes over the *same* result screen (a re-measure after F8, a hot reload, the page's
// own animation re-entering) still collapse into the one file that screen deserves.
internal sealed class SettlementArchive
{
    private IntPtr _page;
    private uint _run;
    private bool _written;
    private string _content;

    internal bool ShouldWrite(IntPtr page, uint run)
        => !_written || _page != page || _run != run;

    internal bool ShouldRefresh(IntPtr page, uint run, string content)
        => _written && _page == page && _run == run && !String.Equals(_content, content, StringComparison.Ordinal);
    internal void MarkWritten(IntPtr page, uint run, string content = null)
    {
        _page = page;
        _run = run;
        _written = true;
        _content = content;
    }

    // Kept for the one case the pair cannot express: an explicit "the archive knows nothing" reset.
    internal void Forget()
    {
        _page = IntPtr.Zero;
        _run = 0u;
        _written = false;
        _content = null;
    }
}
