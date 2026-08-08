using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.Views.Controls;

public class RichTextBlock : TextBlock
{
    private Uri? _primaryLinkUri;

    public static readonly StyledProperty<string?> RichTextProperty =
        AvaloniaProperty.Register<RichTextBlock, string?>(nameof(RichText));

    public string? RichText
    {
        get => GetValue(RichTextProperty);
        set => SetValue(RichTextProperty, value);
    }

    public RichTextBlock()
    {
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RichTextProperty)
        {
            UpdateInlines(RichText);
        }
    }

    private void UpdateInlines(string? text)
    {
        Inlines?.Clear();
        _primaryLinkUri = null;
        Cursor = null;
        if (string.IsNullOrEmpty(text))
            return;

        int i = 0;
        while (i < text.Length)
        {
            if (TryAddFormattedInline(text, ref i))
            {
                continue;
            }

            AddPlainTextUntilNextSpecial(text, ref i);
        }
    }

    private bool TryAddFormattedInline(string text, ref int index)
    {
        return TryAddLineBreak(text, ref index) ||
            TryAddStrikethrough(text, ref index) ||
            TryAddLink(text, ref index) ||
            TryAddBadge(text, ref index) ||
            TryAddBold(text, ref index) ||
            TryAddCode(text, ref index) ||
            TryAddItalic(text, ref index);
    }

    private bool TryAddLineBreak(string text, ref int index)
    {
        if (text[index] != '\n')
            return false;

        Inlines?.Add(new LineBreak());
        index++;
        return true;
    }

    private bool TryAddStrikethrough(string text, ref int index)
    {
        if (!IsAt(text, index, "~~") || !TryReadToken(text, index + 2, "~~", out string? strikeText, out int next))
            return false;

        var run = new Run(strikeText)
        {
            TextDecorations = CreateStrikethrough()
        };
        Inlines?.Add(run);
        index = next;
        return true;
    }

    private bool TryAddLink(string text, ref int index)
    {
        if (!IsAt(text, index, "[") || !TryReadLink(text, index, out string? linkText, out string? linkUrl, out int next))
            return false;

        AddLinkRun(linkText, linkUrl);
        index = next;
        return true;
    }

    private void AddLinkRun(string linkText, string linkUrl)
    {
        if (TryCreateUri(linkUrl, out Uri? uri))
        {
            _primaryLinkUri ??= uri;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        Inlines?.Add(new Run(linkText)
        {
            Foreground = GetAccentBrush() ?? Foreground,
            TextDecorations = CreateUnderline()
        });
    }

    private bool TryAddBadge(string text, ref int index)
    {
        if (!IsAt(text, index, "{") || !TryReadToken(text, index + 1, "}", out string? badgeText, out int next))
            return false;

        Inlines?.Add(new Run($"[{badgeText.Trim()}]")
        {
            Foreground = GetAccentBrush() ?? Foreground
        });
        index = next;
        return true;
    }

    private bool TryAddBold(string text, ref int index)
    {
        if (!IsAt(text, index, "**") || !TryReadToken(text, index + 2, "**", out string? boldText, out int next))
            return false;

        var bold = new Bold();
        bold.Inlines.Add(new Run(boldText));
        Inlines?.Add(bold);
        index = next;
        return true;
    }

    private bool TryAddCode(string text, ref int index)
    {
        if (!IsAt(text, index, "`") || !TryReadToken(text, index + 1, "`", out string? codeText, out int next))
            return false;

        Inlines?.Add(new Run(codeText)
        {
            FontFamily = new FontFamily("Menlo,Consolas,monospace")
        });
        index = next;
        return true;
    }

    private bool TryAddItalic(string text, ref int index)
    {
        if (!IsAt(text, index, "*") || !TryReadToken(text, index + 1, "*", out string? italicText, out int next))
            return false;

        var italic = new Italic();
        italic.Inlines.Add(new Run(italicText));
        Inlines?.Add(italic);
        index = next;
        return true;
    }

    private void AddPlainTextUntilNextSpecial(string text, ref int index)
    {
        int next = FindNextSpecial(text, index);
        string chunk = text[index..next];
        if (chunk.Length > 0)
        {
            Inlines?.Add(new Run(chunk));
        }
        index = next;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_primaryLinkUri is null)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        OpenUrl(_primaryLinkUri);
        e.Handled = true;
    }

    private static bool IsAt(string value, int index, string token)
    {
        if (index + token.Length > value.Length)
            return false;
        return string.Compare(value, index, token, 0, token.Length, StringComparison.Ordinal) == 0;
    }

    private static bool TryReadToken(string value, int start, string token, out string content, out int nextIndex)
    {
        int end = value.IndexOf(token, start, StringComparison.Ordinal);
        if (end < 0)
        {
            content = string.Empty;
            nextIndex = start;
            return false;
        }

        content = value[start..end];
        nextIndex = end + token.Length;
        return true;
    }

    private static int FindNextSpecial(string value, int start)
    {
        int next = value.Length;
        int newline = value.IndexOf('\n', start);
        if (newline >= 0)
            next = Math.Min(next, newline);
        int bold = value.IndexOf("**", start, StringComparison.Ordinal);
        if (bold >= 0)
            next = Math.Min(next, bold);
        int strike = value.IndexOf("~~", start, StringComparison.Ordinal);
        if (strike >= 0)
            next = Math.Min(next, strike);
        int italic = value.IndexOf('*', start);
        if (italic >= 0)
            next = Math.Min(next, italic);
        int code = value.IndexOf('`', start);
        if (code >= 0)
            next = Math.Min(next, code);
        int link = value.IndexOf('[', start);
        if (link >= 0)
            next = Math.Min(next, link);
        int badge = value.IndexOf('{', start);
        if (badge >= 0)
            next = Math.Min(next, badge);
        return next;
    }

    private static bool TryReadLink(string value, int start, out string text, out string url, out int nextIndex)
    {
        text = string.Empty;
        url = string.Empty;
        nextIndex = start;

        int closeBracket = value.IndexOf(']', start + 1);
        if (closeBracket < 0)
            return false;
        if (closeBracket + 1 >= value.Length || value[closeBracket + 1] != '(')
            return false;
        int closeParen = value.IndexOf(')', closeBracket + 2);
        if (closeParen < 0)
            return false;

        string label = value.Substring(start + 1, closeBracket - start - 1);
        string link = value.Substring(closeBracket + 2, closeParen - closeBracket - 2);
        text = string.IsNullOrWhiteSpace(label) ? link : label;
        url = link;
        nextIndex = closeParen + 1;
        return true;
    }

    private static IBrush? GetAccentBrush()
    {
        if (Application.Current is not null &&
            Application.Current.TryFindResource("AccentBrush", out object? value))
        {
            return value as IBrush;
        }

        return null;
    }

    private static TextDecorationCollection CreateUnderline()
        => [new TextDecoration { Location = TextDecorationLocation.Underline }];

    private static TextDecorationCollection CreateStrikethrough()
        => [new TextDecoration { Location = TextDecorationLocation.Strikethrough }];

    private static bool TryCreateUri(string raw, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? candidate) &&
            !Uri.TryCreate($"https://{raw}", UriKind.Absolute, out candidate))
        {
            return false;
        }

        if (!SystemFileLauncher.IsAllowedExternalScheme(candidate.Scheme))
            return false;

        uri = candidate;
        return true;
    }

    private static void OpenUrl(Uri uri)
    {
        try
        {
            SystemFileLauncher.OpenUri(uri.AbsoluteUri);
        }
        catch
        {
            // Best effort: ignore failures to open browser.
        }
    }
}
