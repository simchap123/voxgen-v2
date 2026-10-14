using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace VoxGen.Desktop.ReadAloud;

/// <summary>
/// Finds the readable text under a screen point via Windows UI Automation — works across most
/// apps (browsers, Office, Explorer, WPF/WinForms/UWP). Document-like controls give the paragraph
/// under the cursor; everything else gives its value or accessible name.
///
/// UIA calls are cross-process COM and can block on a hung target app, so callers should run this
/// off the UI thread with a timeout.
/// </summary>
public static class ScreenTextReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    /// <summary>Current cursor position in screen coordinates.</summary>
    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    /// <summary>
    /// Cleaned text under the point. <c>Weak</c> means it's only a container's name (e.g. a window
    /// title) — the caller should try OCR for the real content first.
    /// </summary>
    public readonly record struct ScreenText(string? Text, bool Weak, string Source = "");

    public static ScreenText GetTextAt(int x, int y)
    {
        var point = new System.Windows.Point(x, y);
        AutomationElement element;
        try
        {
            element = AutomationElement.FromPoint(point);
        }
        catch
        {
            return new(null, false);
        }
        if (element is null) return new(null, false);

        // 0. Text the user selected in the app they're pointing at — read exactly that, wherever the
        //    mouse is within it. Uses the focused control, because hosts like Windows Terminal report
        //    only their outer window at the cursor.
        var selected = TrySelectionInSameApp(element);
        if (selected is not null) return new(Clean(selected), false, "selection");

        // Containers (XAML islands, panes) can hide the real text control: drill to the deepest
        // child under the cursor.
        element = DrillDown(element, point);

        // 1. Documents / rich text (browsers, Word, editors): the paragraph under the cursor.
        var text = TryTextPattern(element, point);
        var source = "text-pattern";

        // 2. Edit boxes, combo boxes: their current value.
        if (string.IsNullOrWhiteSpace(text) && element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
        {
            try { text = ((ValuePattern)vp).Current.Value; source = "value"; } catch { /* element went away */ }
        }

        if (!string.IsNullOrWhiteSpace(text)) return new(Clean(text), false, source);

        // 3. Buttons, links, menu items, labels: the accessible name. A container's name (window
        //    title, pane) is a weak answer — the visible content is probably not exposed.
        var weak = false;
        var kind = "?";
        try
        {
            text = element.Current.Name;
            var type = element.Current.ControlType;
            kind = $"{type.ProgrammaticName.Replace("ControlType.", "")}/{element.Current.ClassName}";
            weak = type == ControlType.Window || type == ControlType.Pane || type == ControlType.Document
                   || type == ControlType.Group || type == ControlType.Custom || type == ControlType.TitleBar;
        }
        catch { /* element went away */ }

        return new(Clean(text), weak, "name:" + kind);
    }

    private static string? TrySelectionInSameApp(AutomationElement underCursor)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null || focused.Current.ProcessId != underCursor.Current.ProcessId) return null;
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var tp)) return null;
            var text = string.Concat(Array.ConvertAll(((TextPattern)tp).GetSelection(), r => r.GetText(8000) + " "));
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Walks down to the deepest descendant whose bounds contain the point (max 8 levels).</summary>
    private static AutomationElement DrillDown(AutomationElement element, System.Windows.Point point)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        try
        {
            for (var depth = 0; depth < 8; depth++)
            {
                if (current.TryGetCurrentPattern(TextPattern.Pattern, out _)) return current;
                AutomationElement? hit = null;
                for (var child = walker.GetFirstChild(current); child is not null; child = walker.GetNextSibling(child))
                {
                    var r = child.Current.BoundingRectangle;
                    if (!r.IsEmpty && r.Contains(point)) { hit = child; break; }
                }
                if (hit is null) break;
                current = hit;
            }
        }
        catch
        {
            // element tree changed under us — use what we have
        }
        return current;
    }

    private static string? TryTextPattern(AutomationElement element, System.Windows.Point point)
    {
        // Text often lives on an ancestor (e.g. a span inside a document) — walk up a few levels.
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        for (var depth = 0; current is not null && depth < 4; depth++)
        {
            try
            {
                if (current.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
                {
                    var pattern = (TextPattern)tp;

                    // If the user selected text here, read exactly that — the way to pick "the whole
                    // text" (several paragraphs, a page) rather than one paragraph.
                    try
                    {
                        var selected = string.Concat(Array.ConvertAll(pattern.GetSelection(), r => r.GetText(8000) + " "));
                        if (!string.IsNullOrWhiteSpace(selected)) return selected;
                    }
                    catch { /* control doesn't support selection — fall through */ }

                    var range = pattern.RangeFromPoint(point);
                    range.ExpandToEnclosingUnit(TextUnit.Paragraph);
                    var t = range.GetText(2000);
                    if (!string.IsNullOrWhiteSpace(t)) return t;
                }
                current = walker.GetParent(current);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Collapses whitespace; drops empty or symbol-only strings.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Regex.Replace(text, @"\s+", " ").Trim();
        return Regex.IsMatch(t, @"[\p{L}\p{N}]") ? t : null;
    }
}
