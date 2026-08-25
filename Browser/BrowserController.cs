using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace Nova;

// Attaches to Chrome via its remote debugging port rather than driving a
// separate Playwright-owned browser, which is what lets browser_read/
// browser_fill see and act on tabs already open - at the cost of that debug
// port being a real local control surface, anything on the machine that can
// reach localhost:9222 has full read/control access to the browser. If no
// debug-mode Chrome is reachable, EnsureBrowserAsync launches one itself
// rather than asking the user to do it manually - see there for why it uses
// a dedicated profile.
internal sealed class BrowserController
{
    // "localhost" resolves to the IPv6 loopback (::1) in Playwright's driver on
    // this machine, but Chrome's debug server only binds IPv4 (127.0.0.1) - use
    // the literal IP to avoid ECONNREFUSED despite the port working everywhere else.
    private const string CdpEndpoint = "http://127.0.0.1:9222";

    private static readonly string[] ChromeCandidatePaths =
    [
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    ];

    // A stable (not temp-folder) location so logins/cookies persist across
    // relaunches instead of starting fresh every time.
    private static readonly string DebugProfileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova", "ChromeDebugProfile");

    private IPlaywright? _playwrightInstance;
    private IBrowser? _browserInstance;
    private IPage? _activeBrowserPage;

    // Populated fresh by every ReadAsync call - lets browser_fill/select/
    // check target one exact field by ref instead of only ever the first
    // element matching a label (GetByLabel(label).First), which silently
    // collides whenever a label repeats (every job entry on a form having
    // its own "Month"/"Year", for instance - a genuinely common pattern,
    // not a one-off).
    //
    // Stores IElementHandle, not ILocator, deliberately - an ILocator built
    // from frame.Locator(css).Nth(i) is lazy and *re-resolves against the
    // live DOM on every action*, so if the field count/order changes at all
    // between the read and a later fill (a checkbox becoming visible, a
    // validation message appearing, a field added/removed elsewhere on the
    // page - all common on real dynamic forms), the same ref index can
    // silently land on a *different* field than the one shown in the
    // original read. An ElementHandle is bound to one specific DOM node at
    // capture time, immune to reordering elsewhere on the page - if that
    // exact node is later removed, acting on it fails loudly instead of
    // silently hitting the wrong one.
    //
    // Cleared on every read/navigate/click rather than merged, since a
    // stale ref from before the page changed would point at the wrong
    // element (or nothing at all).
    private readonly Dictionary<string, IElementHandle> _fieldRefs = new();

    // Every clear site below used to just call _fieldRefs.Clear(), which
    // drops Nova's own references to the handles but never tells the
    // browser side it's done with the remote objects they wrap - each one
    // sits referenced (via DisposeAsync) until the page navigates away or
    // the tab closes. Cheap for one form-filling session (dozens of
    // fields, not thousands), but a real, avoidable leak - disposing
    // explicitly here closes it. Best-effort: a handle can legitimately
    // fail to dispose if its own node is already gone (the whole reason
    // it's being replaced), which isn't worth surfacing as an error.
    private async Task ClearFieldRefsAsync()
    {
        foreach (IElementHandle handle in _fieldRefs.Values)
        {
            try
            {
                await handle.DisposeAsync();
            }
            catch
            {
                // Already gone - fine, that's what we're clearing it for.
            }
        }

        _fieldRefs.Clear();
    }

    // Shared by every form-interaction tool (browser_fill/select/check/upload)
    // plus browser_click - each one needs a live tab before doing anything
    // else, and used to repeat this same InvalidateClosedActivePage-then-check
    // pair verbatim. Hands back the resolved page (rather than leaving callers
    // to keep reading the _activeBrowserPage field themselves) so the
    // nullability the check just established stays visible to the compiler
    // all the way through the caller's method, not just at the check site.
    private bool TryGetActivePage([NotNullWhen(true)] out IPage? page, [NotNullWhen(false)] out string? error)
    {
        InvalidateClosedActivePage();
        if (_activeBrowserPage is null)
        {
            page = null;
            error = "No tab is selected yet - use browser_read first (with tab_hint if more than one tab is open).";
            return false;
        }

        page = _activeBrowserPage;
        error = null;
        return true;
    }

    // Shared by every form-interaction tool that accepts an optional
    // field_ref from a prior browser_read - same lookup-or-explain-why-not
    // shape, just what each caller then does with the resolved handle
    // differs (Fill/Check/Upload act on it directly, Select also tries a
    // combobox fallback).
    private bool TryResolveFieldRef(string fieldRef, [NotNullWhen(true)] out IElementHandle? handle, [NotNullWhen(false)] out string? error)
    {
        if (_fieldRefs.TryGetValue(fieldRef, out handle))
        {
            error = null;
            return true;
        }

        error = $"Ref \"{fieldRef}\" isn't valid - the page may have changed since the last browser_read. " +
                "Call browser_read again and use a fresh ref.";
        return false;
    }

    // For the overlay's "CHROME LINKED" status chip - true once a browser
    // tool call has actually connected to (or launched) a debug-mode
    // Chrome, false before that ever happens or after the user closes it.
    public bool IsConnected => _browserInstance is not null && _browserInstance.IsConnected;

    // The real browser window's handle, found once via WindowActivator's
    // snapshot-diff at launch (confirmed by direct testing: the chrome.exe
    // process Process.Start() hands back exits within ~3s having never
    // owned a window at all - Chrome's launcher re-execs into a separate
    // browser process under a different PID, so tracking "the process we
    // started" can't work here). Reused on every NavigateAsync/ReadAsync so
    // the window surfaces each time Nova actually does something in it, not
    // just once at first launch. A stale handle (window closed) is safe to
    // pass to the Win32 calls - they just no-op.
    private IntPtr _chromeWindowHandle;

    // defaultToNewTab: NovaAssistant passes true for a task's first
    // browser_navigate call (see its own _taskHasNavigatedBrowser) - a new
    // task defaults to a fresh tab rather than silently navigating away
    // from whatever the currently-selected tab still has open (a half-filled
    // form from an earlier, unrelated task), unless the model explicitly
    // set force_new itself, which always wins either way.
    public async Task<string> NavigateAsync(IReadOnlyDictionary<string, JsonElement> input, bool defaultToNewTab = false)
    {
        string url = input["url"].GetString()!;
        bool forceNew = ToolInput.GetBool(input, "force_new") ?? defaultToNewTab;
        IBrowser browser = await EnsureBrowserAsync();
        InvalidateClosedActivePage();
        WindowActivator.BringToFrontMaximized(_chromeWindowHandle);
        await ClearFieldRefsAsync(); // stale refs from whatever page was open before

        if (!forceNew)
        {
            IPage? alreadyOpen = ListOpenTabs(browser).FirstOrDefault(tab => UrlsMatch(tab.Url, url));
            if (alreadyOpen is not null)
            {
                _activeBrowserPage = alreadyOpen;
                return $"This page was already open in another tab (title: \"{await alreadyOpen.TitleAsync()}\") - " +
                       "switched to it instead of opening a duplicate. Ask the user if they'd actually like a " +
                       "fresh tab instead; if they say yes, call browser_navigate again with force_new set to true.";
            }
        }

        if (_activeBrowserPage is null || forceNew)
        {
            IBrowserContext? context = browser.Contexts.FirstOrDefault();
            if (context is null)
            {
                return "Chrome is connected but has no open windows to open a tab in.";
            }

            _activeBrowserPage = await context.NewPageAsync();
        }

        await _activeBrowserPage.GotoAsync(url);
        return $"Navigated to {url}. Title: {await _activeBrowserPage.TitleAsync()}";
    }

    public async Task<string> ReadAsync(IReadOnlyDictionary<string, JsonElement> input)
    {
        IBrowser browser = await EnsureBrowserAsync();
        InvalidateClosedActivePage();
        WindowActivator.BringToFrontMaximized(_chromeWindowHandle);
        string? hint = ToolInput.GetString(input, "tab_hint");
        List<IPage> tabs = ListOpenTabs(browser);

        if (tabs.Count == 0)
        {
            return "Chrome is connected but has no open tabs.";
        }

        if (hint is null && _activeBrowserPage is null && tabs.Count > 1)
        {
            var listings = new List<string>();
            foreach (IPage tab in tabs)
            {
                listings.Add($"- {await tab.TitleAsync()} ({tab.Url})");
            }

            return "Multiple tabs are open - call browser_read again with tab_hint to pick one:\n" + string.Join("\n", listings);
        }

        IPage target;
        if (hint is not null)
        {
            IPage? match = null;
            foreach (IPage tab in tabs)
            {
                string title = await tab.TitleAsync();
                if (title.Contains(hint, StringComparison.OrdinalIgnoreCase) || tab.Url.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    match = tab;
                    break;
                }
            }

            if (match is null)
            {
                return $"No open tab matched \"{hint}\".";
            }

            target = match;
        }
        else
        {
            target = _activeBrowserPage ?? tabs[0];
        }

        _activeBrowserPage = target;
        await ClearFieldRefsAsync();

        // 8000 was starving long application-form pages - a real one
        // (Greenhouse/Workday-style, several sections) routinely ran past
        // it, cutting off before the field list even started and leaving
        // Nova unable to see (let alone fill) most of the form. Raised
        // well past what's actually been seen in practice - trivial
        // relative to Claude's own context window, so there's no reason
        // to be this stingy.
        const int MaxChars = 32000;
        string header = $"URL: {target.Url}\nTitle: {await target.TitleAsync()}\n\n";
        string snapshot = await target.Locator("body").AriaSnapshotAsync();

        // Application forms (Greenhouse, Lever, etc.) are commonly rendered inside
        // an embedded iframe - the main-frame snapshot above won't see into it at
        // all, so the form fields would otherwise be invisible to Nova entirely.
        foreach (IFrame frame in target.Frames)
        {
            if (frame == target.MainFrame)
            {
                continue;
            }

            try
            {
                string frameSnapshot = await frame.Locator("body").AriaSnapshotAsync();
                snapshot += $"\n\n--- embedded frame ({frame.Url}) ---\n{frameSnapshot}";
            }
            catch
            {
                // Frame has no body yet, is detached, or isn't readable - skip it
                // rather than failing the whole read over one sub-frame.
            }
        }

        // The field list matters more for actually filling the form than
        // the raw visual snapshot text does, so if something still has to
        // give at MaxChars, it's the snapshot that gets cut, never the
        // field list - the opposite of what a single trailing truncation
        // over everything combined would do.
        string fieldList = await BuildFieldRefListAsync(target);
        int snapshotBudget = MaxChars - header.Length - fieldList.Length;
        if (snapshot.Length > snapshotBudget)
        {
            snapshot = snapshot[..Math.Max(0, snapshotBudget)] + "\n... [snapshot truncated - field list below is complete]";
        }

        return header + snapshot + fieldList;
    }

    // Enumerates every fillable field (input/select/textarea) across the
    // main frame and any embedded frames, assigns each a stable ref, and
    // stashes the live locator in _fieldRefs so FillAsync can target it
    // exactly - see that field's doc comment for why this exists at all.
    // Uses each element's own .labels association (the same thing a real
    // screen reader resolves) rather than parsing the ARIA snapshot text
    // above, since that's Playwright's own generated format, not something
    // meant to be picked back apart.
    // A closed dropdown's real choices aren't visible text anywhere on the
    // page - without this, browser_select's `value` was a guess based on
    // the field's *label* (e.g. inventing "In Office" for a field whose
    // real options were "Open to commuting to closest hub office" /
    // "Fully remote"), not something Claude ever actually saw. A native
    // <select>'s <option> list is always present in the DOM regardless of
    // open/closed state, so it costs nothing to read here - custom
    // combobox widgets (Greenhouse/Workday-style) don't get the same
    // treatment, since their options are commonly only rendered once
    // opened; browser_select's own description tells Claude what to do
    // when a field has no listed options.
    private const int MaxListedOptions = 30;

    private async Task<string> BuildFieldRefListAsync(IPage page)
    {
        var entries = new List<(string Ref, string Name, string? Options)>();
        int counter = 0;

        foreach (IFrame frame in page.Frames)
        {
            ILocator fields = frame.Locator(
                "input:not([type=hidden]):not([type=submit]):not([type=button]):not([type=reset]), select, textarea");
            int count;
            try
            {
                count = await fields.CountAsync();
            }
            catch
            {
                continue; // frame not readable - same tolerance as the snapshot loop above
            }

            for (int i = 0; i < count; i++)
            {
                ILocator field = fields.Nth(i);
                string name;
                string? options;
                IElementHandle? handle;
                try
                {
                    name = await field.EvaluateAsync<string>(
                        "el => (el.labels && el.labels[0] && el.labels[0].innerText.trim()) || " +
                        "el.getAttribute('aria-label') || el.placeholder || el.name || el.id || '(unlabeled field)'");
                    options = await field.EvaluateAsync<string?>(
                        "el => el.tagName === 'SELECT' ? Array.from(el.options).map(o => o.text.trim()).filter(t => t).join(' | ') : null");
                    // Captured now, while .Nth(i) still resolves to the right
                    // element - see _fieldRefs' doc comment for why this is
                    // stored instead of the lazy locator itself.
                    handle = await field.ElementHandleAsync();
                }
                catch
                {
                    continue; // detached mid-enumeration - skip rather than fail the whole read
                }

                if (handle is null)
                {
                    continue;
                }

                counter++;
                string refId = $"f{counter}";
                _fieldRefs[refId] = handle;
                entries.Add((refId, name, options));
            }
        }

        if (entries.Count == 0)
        {
            return string.Empty;
        }

        Dictionary<string, int> countByName = entries.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.Count());
        var seenSoFar = new Dictionary<string, int>();
        var lines = new List<string>();
        foreach ((string refId, string name, string? options) in entries)
        {
            string display = name;
            if (countByName[name] > 1)
            {
                seenSoFar.TryGetValue(name, out int soFar);
                soFar++;
                seenSoFar[name] = soFar;
                display = $"{name} ({soFar} of {countByName[name]})";
            }

            if (!string.IsNullOrEmpty(options))
            {
                string[] optionArray = options.Split(" | ");
                string optionsText = optionArray.Length > MaxListedOptions
                    ? string.Join(" | ", optionArray.Take(MaxListedOptions)) + $" | ... ({optionArray.Length - MaxListedOptions} more)"
                    : options;
                display += $" [options: {optionsText}]";
            }

            lines.Add($"  {refId}: {display}");
        }

        return "\n\nFillable fields (pass ref to browser_fill to target one exactly - required whenever a " +
               "label repeats, like separate Month/Year fields per entry in a repeated form section):\n" +
               string.Join("\n", lines);
    }

    public async Task<string> FillAsync(IReadOnlyDictionary<string, JsonElement> input)
    {
        string label = input["label"].GetString()!;
        string value = input["value"].GetString()!;
        string? fieldRef = ToolInput.GetString(input, "field_ref");
        if (!TryGetActivePage(out IPage? page, out string? noTabError))
        {
            return noTabError;
        }

        if (fieldRef is not null)
        {
            if (!TryResolveFieldRef(fieldRef, out IElementHandle? handle, out string? error))
            {
                return error;
            }

            await handle.FillAsync(value);
            return $"Filled \"{label}\".";
        }

        // No ref given - fine for a field whose label is unique on the
        // page, but this always hits the *first* match, silently colliding
        // whenever a label repeats (e.g. every entry in a repeated form
        // section having its own "Month"). Use the ref from browser_read's
        // field list in that case instead.
        ILocator? fieldLocator = null;
        foreach (IFrame frame in page.Frames)
        {
            ILocator candidate = frame.GetByLabel(label);
            if (await candidate.CountAsync() > 0)
            {
                fieldLocator = candidate.First;
                break;
            }
        }

        if (fieldLocator is null)
        {
            return $"No field labeled \"{label}\" found on the page or in any embedded frame.";
        }

        await fieldLocator.FillAsync(value);
        return $"Filled \"{label}\".";
    }

    // Same free-to-use, no-Gate-1 status as browser_fill and the same
    // reasoning: choosing a dropdown option or ticking a checkbox is still
    // just filling in form data for the user to review, not submitting or
    // leaving the machine - it was an oversight that only text fields had
    // a tool, not a deliberate restriction on these two input types.
    public async Task<string> SelectAsync(IReadOnlyDictionary<string, JsonElement> input)
    {
        string label = input["label"].GetString()!;
        string value = input["value"].GetString()!;
        string? fieldRef = ToolInput.GetString(input, "field_ref");
        if (!TryGetActivePage(out IPage? page, out string? noTabError))
        {
            return noTabError;
        }

        if (fieldRef is not null)
        {
            if (!TryResolveFieldRef(fieldRef, out IElementHandle? handle, out string? error))
            {
                return error;
            }

            try
            {
                await handle.SelectOptionAsync(new SelectOptionValue { Label = value });
                return $"Selected \"{value}\" for \"{label}\".";
            }
            catch
            {
                // Not a real <select> - Greenhouse and similar ATSes commonly
                // build their own combobox widget instead (see below).
            }

            string? comboError = await TryComboboxSelectAsync(handle, value);
            return comboError is null
                ? $"Selected \"{value}\" for \"{label}\" (typed into its custom dropdown and picked the matching option from the popup)."
                : $"Couldn't select \"{value}\" for \"{label}\" - tried both a native dropdown and a custom typeahead popup, neither worked ({comboError}). May need a manual click.";
        }

        // No ref given - fine for a dropdown whose label is unique on the
        // page; always hits the first match otherwise, same caveat as
        // browser_fill's label-only path.
        foreach (IFrame frame in page.Frames)
        {
            ILocator candidate = frame.GetByLabel(label);
            if (await candidate.CountAsync() > 0)
            {
                try
                {
                    await candidate.First.SelectOptionAsync(new SelectOptionValue { Label = value });
                    return $"Selected \"{value}\" for \"{label}\".";
                }
                catch
                {
                }

                IElementHandle? handle = await candidate.First.ElementHandleAsync();
                string? comboError = handle is null
                    ? "couldn't resolve the field itself"
                    : await TryComboboxSelectAsync(handle, value);
                return comboError is null
                    ? $"Selected \"{value}\" for \"{label}\" (typed into its custom dropdown and picked the matching option from the popup)."
                    : $"Couldn't select \"{value}\" for \"{label}\" - tried both a native dropdown and a custom typeahead popup, neither worked ({comboError}). May need a manual click.";
            }
        }

        // GetByLabel found nothing at all - the common shape for a
        // radio-button/toggle-pill question group (Yes/No, work
        // authorization, relocation, etc., frequent on Ashby and similar
        // ATSes): there's no single form control associated with the
        // question text the way a real <label> targets one input, just a
        // heading/legend above a set of separately-labeled options ("Yes",
        // "No"), so GetByLabel(the question) has nothing to find. Confirmed
        // live as a real gap: this used to mean falling back to
        // browser_click, which structurally refuses anything that isn't a
        // recognized navigational action - not because clicking a Yes/No
        // answer is unsafe (it's exactly as reversible as any other form
        // field the user reviews before submitting), just because no tool
        // covered this specific widget shape. Same free-to-use reasoning as
        // the rest of this method - still just answering a form question,
        // not submitting anything.
        string? radioGroupError = await TryRadioGroupSelectAsync(page, label, value);
        if (radioGroupError is null)
        {
            return $"Selected \"{value}\" for \"{label}\" (radio-button/toggle question group).";
        }

        return $"No dropdown, combobox, or radio-button group labeled \"{label}\" found on the page or in any " +
               $"embedded frame ({radioGroupError}).";
    }

    // See the doc comment at this method's one call site. Looks for a
    // properly-accessible radiogroup first (role="radiogroup" with an
    // accessible name matching the question text - what a well-built ATS
    // form uses), then the matching individual option by its own visible
    // text within that group. Doesn't attempt a looser page-wide fallback
    // the way TryComboboxSelectAsync does - a bare role=Radio search with
    // no group scoping risks clicking the wrong question's "Yes" entirely
    // when a form has several similar-shaped questions on one page, which
    // is worse than just reporting "couldn't find it."
    // Bounded click with a force-click fallback on timeout - originally
    // built for TryRadioGroupSelectAsync below, now shared by every site
    // that clicks an arbitrary Button/Link/Option/Radio element found on a
    // real-world form. Confirmed live as a real hang, not a guess: a plain
    // unbounded click ran the full 60 seconds three times in a row on a
    // real Ashby form before the outer tool timeout finally killed it - the
    // native input a lot of component libraries build their custom-styled
    // controls on top of is commonly visually hidden (zero size, opacity 0)
    // behind the styled sibling the user actually sees, and Playwright's
    // normal click retries its own visibility/stability check indefinitely
    // against that hidden element, since it can never pass. Failing fast
    // and retrying with Force=true is safe specifically because the caller
    // already resolved `locator`/`element` to a real, confirmed match by
    // role/text before calling this - this only skips the part of the
    // check that a hidden-input-behind-a-styled-sibling pattern fails for
    // reasons that have nothing to do with whether it's the right control.
    private static async Task ClickWithFallbackAsync(ILocator locator, int timeoutMs = 3000)
    {
        try
        {
            await locator.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs });
        }
        catch (TimeoutException)
        {
            await locator.ClickAsync(new LocatorClickOptions { Force = true, Timeout = timeoutMs });
        }
    }

    // Same shape as the ILocator overload above, for the one call site
    // (TryComboboxSelectAsync) that's still holding an IElementHandle
    // rather than a Locator - see _fieldRefs' own doc comment for why that
    // site can't just re-resolve a fresh Locator instead.
    private static async Task ClickWithFallbackAsync(IElementHandle element, int timeoutMs = 3000)
    {
        try
        {
            await element.ClickAsync(new ElementHandleClickOptions { Timeout = timeoutMs });
        }
        catch (TimeoutException)
        {
            await element.ClickAsync(new ElementHandleClickOptions { Force = true, Timeout = timeoutMs });
        }
    }

    private static async Task<string?> TryRadioGroupSelectAsync(IPage page, string label, string value)
    {
        try
        {
            ILocator group = page.GetByRole(AriaRole.Radiogroup, new PageGetByRoleOptions { Name = label, Exact = false });
            if (await group.CountAsync() == 0)
            {
                return "no radiogroup found with a matching accessible name";
            }

            ILocator option = group.First.GetByRole(AriaRole.Radio, new LocatorGetByRoleOptions { Name = value, Exact = false });
            await option.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 700 });
            await ClickWithFallbackAsync(option.First);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message.Split('\n')[0];
        }
    }

    // Greenhouse and several other ATSes build their own combobox instead
    // of a real <select>, and not always the same way - some are
    // typeahead widgets (type to filter a popup listbox), others are
    // button/pill pickers where clicking the field alone reveals the
    // choices with nothing to type. Typed text alone never commits a
    // value either way - the matching option has to actually be clicked,
    // confirmed the hard way during a real session (typing "No" left the
    // field showing "Select..." even though the text visually looked
    // entered). Tries click-then-look-for-a-match first (covers the
    // button/pill style), then type-then-look-for-a-match (covers
    // typeahead) - and within each, checks progressively looser ways of
    // finding "the option that says value", since these widgets don't all
    // use proper ARIA roles on their popup items.
    private static async Task<string?> TryComboboxSelectAsync(IElementHandle field, string value)
    {
        IFrame? frame;
        try
        {
            frame = await field.OwnerFrameAsync();
        }
        catch (Exception ex)
        {
            return ex.Message.Split('\n')[0];
        }

        if (frame is null)
        {
            return "couldn't resolve the field's frame";
        }

        IPage page = frame.Page;

        try
        {
            await ClickWithFallbackAsync(field);
            if (await TryClickMatchingOptionAsync(page, value))
            {
                return null;
            }

            await field.FillAsync(string.Empty);
            // Playwright marks ElementHandle.TypeAsync obsolete in favor of
            // Locator.PressSequentiallyAsync - not used here on purpose:
            // switching to a Locator would mean re-resolving against the
            // live DOM instead of this specific captured element, which is
            // exactly the staleness bug _fieldRefs' doc comment describes
            // fixing. This is the only per-keystroke typing API that stays
            // bound to one exact node.
            await field.TypeAsync(value, new ElementHandleTypeOptions { Delay = 30 });
            if (await TryClickMatchingOptionAsync(page, value))
            {
                return null;
            }

            return "no matching option appeared in any popup after opening or typing into the field";
        }
        catch (Exception ex)
        {
            return ex.Message.Split('\n')[0];
        }
    }

    // Tries the properly-ARIA-labeled case first, then progressively
    // looser roles, then finally a raw visible-text match with no role
    // requirement at all - covers everything from a well-built combobox
    // down to a custom widget with no meaningful accessibility semantics
    // on its popup items.
    private static async Task<bool> TryClickMatchingOptionAsync(IPage page, string value)
    {
        AriaRole[] rolesToTry = [AriaRole.Option, AriaRole.Menuitem, AriaRole.Listitem, AriaRole.Button, AriaRole.Radio];
        foreach (AriaRole role in rolesToTry)
        {
            try
            {
                ILocator candidate = page.GetByRole(role, new PageGetByRoleOptions { Name = value, Exact = false });
                await candidate.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 700 });
                await ClickWithFallbackAsync(candidate.First);
                return true;
            }
            catch
            {
                // Try the next role.
            }
        }

        try
        {
            ILocator textMatch = page.GetByText(value, new PageGetByTextOptions { Exact = false }).Last;
            await textMatch.WaitForAsync(new LocatorWaitForOptions { Timeout = 700, State = WaitForSelectorState.Visible });
            await ClickWithFallbackAsync(textMatch);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // See SelectAsync's doc comment - same free-to-use reasoning applies
    // here.
    public async Task<string> CheckAsync(IReadOnlyDictionary<string, JsonElement> input)
    {
        string label = input["label"].GetString()!;
        bool shouldCheck = ToolInput.GetBool(input, "checked") ?? true;
        string? fieldRef = ToolInput.GetString(input, "field_ref");
        if (!TryGetActivePage(out IPage? page, out string? noTabError))
        {
            return noTabError;
        }

        string verb = shouldCheck ? "Checked" : "Unchecked";

        if (fieldRef is not null)
        {
            if (!TryResolveFieldRef(fieldRef, out IElementHandle? handle, out string? error))
            {
                return error;
            }

            if (shouldCheck)
            {
                await handle.CheckAsync();
            }
            else
            {
                await handle.UncheckAsync();
            }

            return $"{verb} \"{label}\".";
        }

        foreach (IFrame frame in page.Frames)
        {
            ILocator candidate = frame.GetByLabel(label);
            if (await candidate.CountAsync() > 0)
            {
                if (shouldCheck)
                {
                    await candidate.First.CheckAsync();
                }
                else
                {
                    await candidate.First.UncheckAsync();
                }

                return $"{verb} \"{label}\".";
            }
        }

        return $"No checkbox labeled \"{label}\" found on the page or in any embedded frame.";
    }

    // Same free-to-use reasoning as SelectAsync/CheckAsync - uploading a
    // file the user already has (a resume, a generated cover letter) into
    // a file-picker field is still just filling in form data for the user
    // to review, not submitting anything. Playwright's SetInputFilesAsync
    // works on the underlying <input type="file"> directly regardless of
    // whether it's visually hidden behind a styled custom widget (the
    // common pattern for these fields), which is why this can reach fields
    // that read as something else entirely (a div, a button) in the page
    // snapshot.
    public async Task<string> UploadAsync(IReadOnlyDictionary<string, JsonElement> input)
    {
        string label = input["label"].GetString()!;
        string filePath = input["file_path"].GetString()!;
        string? fieldRef = ToolInput.GetString(input, "field_ref");
        if (!TryGetActivePage(out IPage? page, out string? noTabError))
        {
            return noTabError;
        }

        if (!File.Exists(filePath))
        {
            return $"File not found: \"{filePath}\". Use its full local path.";
        }

        if (fieldRef is not null)
        {
            if (!TryResolveFieldRef(fieldRef, out IElementHandle? handle, out string? error))
            {
                return error;
            }

            await handle.SetInputFilesAsync(filePath);
            return $"Uploaded \"{Path.GetFileName(filePath)}\" to \"{label}\".";
        }

        foreach (IFrame frame in page.Frames)
        {
            ILocator candidate = frame.GetByLabel(label);
            if (await candidate.CountAsync() > 0)
            {
                await candidate.First.SetInputFilesAsync(filePath);
                return $"Uploaded \"{Path.GetFileName(filePath)}\" to \"{label}\".";
            }
        }

        return $"No upload field labeled \"{label}\" found by label alone - it's often hidden behind a " +
               "styled widget. Use field_ref from browser_read's \"Fillable fields\" list instead.";
    }

    // Deliberately narrow: only navigational clicks (pagination, expand/
    // collapse, show more, close/dismiss) ever reach Playwright at all -
    // see NavigationalClickGuard for why this is a hard check, not a prompt
    // instruction. Everything else about browser_click stays the same
    // structural line as before: no click tool exists for anything that
    // could submit, purchase, post, or act on the user's behalf.
    public async Task<string> ClickAsync(IReadOnlyDictionary<string, JsonElement> input)
    {
        string label = input["label"].GetString()!;
        if (!NavigationalClickGuard.IsAllowed(label, out string? refusalReason))
        {
            return refusalReason!;
        }

        if (!TryGetActivePage(out IPage? page, out string? noTabError))
        {
            return noTabError;
        }

        foreach (IFrame frame in page.Frames)
        {
            foreach (AriaRole role in new[] { AriaRole.Button, AriaRole.Link })
            {
                ILocator candidate = frame.GetByRole(role, new FrameGetByRoleOptions { Name = label });
                if (await candidate.CountAsync() > 0)
                {
                    await ClickWithFallbackAsync(candidate.First);
                    // Every allowed label here exists specifically to change
                    // page/step state (pagination, expand/collapse, "Add
                    // Another", "Back") - any cached field refs from before
                    // this click can no longer be trusted to point at the
                    // same fields, so don't let a stale one silently
                    // succeed against whatever's there now.
                    await ClearFieldRefsAsync();
                    return $"Clicked \"{label}\". If you had field refs from an earlier browser_read, they're no " +
                           "longer valid - read the page again before filling anything else.";
                }
            }
        }

        return $"No clickable button or link labeled \"{label}\" found on the page or in any embedded frame.";
    }

    private async Task<IBrowser> EnsureBrowserAsync()
    {
        // _browserInstance/_activeBrowserPage are cached across tool calls, but the
        // user can close that Chrome window at any time. Without this check, a
        // stale disconnected IBrowser would be returned forever - even after a
        // fresh debug Chrome comes up - since the null check below would never
        // fall through to reconnect.
        if (_browserInstance is not null && !_browserInstance.IsConnected)
        {
            _browserInstance = null;
            _activeBrowserPage = null;
        }

        if (_browserInstance is not null)
        {
            return _browserInstance;
        }

        _playwrightInstance ??= await Playwright.CreateAsync();

        IBrowser? connected = await TryConnectAsync();
        if (connected is not null)
        {
            _browserInstance = connected;
            return connected;
        }

        // Nothing reachable yet - launch a debug-mode Chrome ourselves rather
        // than asking the user to do it manually. Always uses its own
        // dedicated profile: Chrome 136+ silently ignores
        // --remote-debugging-port on the default profile for security
        // reasons, so this can never be turned on for the user's real,
        // already-running Chrome window - a separate instance is the only
        // way this works at all, not just a convenience choice.
        string? chromePath = ChromeCandidatePaths.FirstOrDefault(File.Exists);
        if (chromePath is null)
        {
            throw new InvalidOperationException(
                "Couldn't find Chrome installed in the usual location, so I can't launch a debug " +
                "instance automatically. Install Chrome, or launch it yourself with " +
                $"--remote-debugging-port=9222 --user-data-dir=\"{DebugProfileDir}\".");
        }

        Directory.CreateDirectory(DebugProfileDir);
        var psi = new ProcessStartInfo(chromePath, $"--remote-debugging-port=9222 --user-data-dir=\"{DebugProfileDir}\"")
        {
            UseShellExecute = false,
        };
        // Must start watching *before* Process.Start - see _chromeWindowHandle's
        // doc comment for why tracking the launched process itself doesn't work.
        WindowActivator.WatchAndActivateNewWindow("chrome", hWnd => _chromeWindowHandle = hWnd);
        Process.Start(psi);

        for (int attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(500);
            connected = await TryConnectAsync();
            if (connected is not null)
            {
                _browserInstance = connected;
                return connected;
            }
        }

        throw new InvalidOperationException(
            "Launched Chrome but couldn't connect to its debug port after several seconds - something's " +
            "blocking it (a firewall, security software, or the launch failing silently).");
    }

    private async Task<IBrowser?> TryConnectAsync()
    {
        try
        {
            return await _playwrightInstance!.Chromium.ConnectOverCDPAsync(CdpEndpoint);
        }
        catch
        {
            return null;
        }
    }

    private static List<IPage> ListOpenTabs(IBrowser browser) => browser.Contexts.SelectMany(context => context.Pages).ToList();

    private static bool UrlsMatch(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    // The selected tab can be closed by the user without the browser itself
    // disconnecting - without this, FillAsync/NavigateAsync would throw on a
    // closed page instead of falling back to picking/opening one.
    private void InvalidateClosedActivePage()
    {
        if (_activeBrowserPage is { IsClosed: true })
        {
            _activeBrowserPage = null;
        }
    }
}
