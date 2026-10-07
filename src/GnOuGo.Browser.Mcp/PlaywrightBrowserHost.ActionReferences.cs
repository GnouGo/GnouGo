using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace GnOuGo.Browser.Mcp;

public sealed record BrowserObservedTarget(string Reference, string? SnapshotId, string RequestedAction,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectionCause = null);

internal sealed class BrowserActionReferenceException(string code, string message) : InvalidOperationException(message)
{
    internal string Code { get; } = code;
}

public sealed partial class PlaywrightBrowserHost
{
    private readonly List<IJSHandle> _retiredObservationHandles = [];

    // Native semantics and explicit ARIA roles, never labels, URLs or site names.
    // The same signature is captured and checked immediately before interaction.
    private const string ObservedActionStateScript = """
        e => {
          const tag = e.tagName.toLowerCase(), role = (e.getAttribute('role') || '').trim().toLowerCase();
          const type = (e.getAttribute('type') || '').toLowerCase();
          const disabled = e.matches(':disabled') || e.closest('[inert],[aria-disabled="true"]') !== null;
          const readonly = e.readOnly === true || e.getAttribute('aria-readonly') === 'true';
          const editable = !readonly && (tag === 'textarea' || e.isContentEditable ||
            tag === 'input' && !['button','submit','reset','checkbox','radio','file','hidden','image','range','color'].includes(type));
          const nativeControl = tag === 'button' || tag === 'input' && ['button','submit','reset','checkbox','radio','image'].includes(type);
          const controlRole = ['button','checkbox','radio','switch','tab','menuitem','menuitemcheckbox','menuitemradio'].includes(role);
          const follows = tag === 'a' && e.hasAttribute('href') && (role === '' || role === 'link');
          const actions = [];
          if (!disabled) {
            if (controlRole || nativeControl && (role === '' || controlRole)) actions.push('activate');
            if (follows) actions.push('follow');
            if (editable && (role === '' || ['textbox','searchbox','combobox','spinbutton'].includes(role))) actions.push('fill');
            if (tag === 'select') actions.push('select');
            if (actions.length || e.tabIndex >= 0) actions.push('press');
          }
          const signature = JSON.stringify([tag, role, type, e.href || null, disabled, readonly, e.isContentEditable,
            e.getAttribute('aria-label'), e.getAttribute('placeholder'), (e.innerText || '').replace(/\s+/g,' ').trim(),
            e.getAttribute('name'), e.getAttribute('formaction'), e.getAttribute('target'),
            e.form ? [e.form.action, e.form.method, e.form.target] : null]);
          return {actions, signature};
        }
        """;

    private async Task ReleaseBrowserGateAsync(IJSHandle? activeHandle = null)
    {
        try { await DisposeRetiredObservationHandlesAsync(activeHandle); }
        finally { _gate.Release(); }
    }

    private async Task DisposeRetiredObservationHandlesAsync(IJSHandle? activeHandle = null)
    {
        IJSHandle[] retired;
        lock (_observationLock) { if (activeHandle is not null) _retiredObservationHandles.Add(activeHandle); retired = _retiredObservationHandles.ToArray(); _retiredObservationHandles.Clear(); }
        foreach (var handle in retired)
            try { await handle.DisposeAsync(); }
            catch (PlaywrightException) { /* Navigation/context closure may already have released it. */ }
    }

    private async Task<(IElementHandle Element, BrowserObservationRecord Record)> ResolveObservedActionAsync(
        string? selector, string reference, string action, CancellationToken ct)
    {
        if (selector is not null) throw new BrowserActionReferenceException("INVALID_REFERENCE", "Specify either a selector or an observed reference, never both.");
        var parts = reference.Split(':');
        if (parts.Length != 3 || parts[1] != "record" || !Guid.TryParseExact(parts[0], "N", out _) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
            throw new BrowserActionReferenceException("INVALID_REFERENCE", "The observed reference is malformed.");
        ObservationSnapshot snapshot;
        BrowserObservationRecord record;
        lock (_observationLock)
        {
            if (_expiredObservations.TryGetValue(parts[0], out var expired)) throw Expired(expired);
            snapshot = _observation ?? throw new BrowserActionReferenceException("INVALID_REFERENCE", "No matching observation exists in this Browser session.");
            if (snapshot.Id != parts[0] || index >= snapshot.Capture.Records.Count || !snapshot.DeliveredReferences.Contains(reference))
                throw new BrowserActionReferenceException("INVALID_REFERENCE", "The reference was not delivered by the current observation.");
            EnsureCurrent(snapshot.Stamp);
            record = snapshot.Capture.Records[index];
        }
        if (record.Actions?.Contains(action, StringComparer.Ordinal) != true)
            throw new BrowserActionReferenceException("ACTION_MISMATCH", "The observed element does not support the requested " + action + " action.");
        IElementHandle? element = null;
        try
        {
            var handle = await snapshot.Handle.EvaluateHandleAsync("(capture, index) => capture.elements[index]", index).WaitAsync(ct);
            element = handle.AsElement();
            if (element is null) { await handle.DisposeAsync(); throw new BrowserActionReferenceException("INVALID_REFERENCE", "The observed element is unavailable."); }
            var capturedState = await snapshot.Handle.EvaluateAsync<string>("(capture, index) => capture.states[index]", index).WaitAsync(ct);
            var valid = await element.EvaluateAsync<bool>("(e, state) => e.isConnected && (" + ObservedActionStateScript + ")(e).signature === state", capturedState).WaitAsync(ct);
            EnsureCurrent(snapshot.Stamp);
            if (!valid) throw new BrowserActionReferenceException("REFERENCE_CHANGED", "The observed element was detached, replaced or changed. Obtain a fresh observation.");
            if (!await element.IsVisibleAsync().WaitAsync(ct) || !await element.IsEnabledAsync().WaitAsync(ct) ||
                action == "fill" && !await element.IsEditableAsync().WaitAsync(ct))
                throw new BrowserActionReferenceException("ACTION_MISMATCH", "The observed element is not currently actionable.");
            if (action == "follow") BrowserNavigationPolicy.ValidateNavigationTarget(record.Href!, _settings);
            EnsureCurrent(snapshot.Stamp);
            return (element, record);
        }
        catch
        {
            if (element is not null) await element.DisposeAsync();
            EnsureCurrent(snapshot.Stamp);
            throw;
        }
    }

    private static async Task<bool> DetermineSubmittedAsync(IElementHandle element)
    {
        var values = await element.EvaluateAsync<string[]>("e => [e.tagName, e.getAttribute('type') || '', e.form || e.closest('form') || e.getAttribute('form') ? 'yes' : 'no']");
        return BrowserActionSemantics.LooksLikeSubmitControl(values[0], values[1], values[2] == "yes");
    }

    internal static BrowserObservedTarget? ObservedTarget(string? reference, string action, string? cause = null)
    {
        if (reference is null) return null;
        var id = reference.Split(':')[0];
        var target = new BrowserObservedTarget(reference, Guid.TryParseExact(id, "N", out _) ? id : null, action, cause);
        Activity.Current?.SetTag("browser.action.reference", reference);
        Activity.Current?.SetTag("browser.action.snapshot_id", target.SnapshotId);
        Activity.Current?.SetTag("browser.action.requested_action", action);
        Activity.Current?.SetTag("browser.action.rejection_cause", cause);
        return target;
    }
}
