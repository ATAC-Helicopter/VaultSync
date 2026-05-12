using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;

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
            if (text[i] == '\n')
            {
                Inlines?.Add(new LineBreak());
                i++;
                continue;
            }

            if (IsAt(text, i, "~~") && TryReadToken(text, i + 2, "~~", out string? strikeText, out int nextStrike))
            {
                var run = new Run(strikeText)
                {
                    TextDecorations = CreateStrikethrough()
                };
                Inlines?.Add(run);
                i = nextStrike;
                continue;
            }

            if (IsAt(text, i, "[") && TryReadLink(text, i, out string? linkText, out string? linkUrl, out int nextLink))
            {
                if (TryCreateUri(linkUrl, out Uri? uri))
                {
                    _primaryLinkUri ??= uri;
                    Cursor = new Cursor(StandardCursorType.Hand);
                    var run = new Run(linkText)
                    {
                        Foreground = GetAccentBrush() ?? Foreground,
                        TextDecorations = CreateUnderline()
                    };
                    Inlines?.Add(run);
                }
                else
                {
                    var run = new Run(linkText)
                    {
                        Foreground = GetAccentBrush() ?? Foreground,
                        TextDecorations = CreateUnderline()
                    };
                    Inlines?.Add(run);
                }
                i = nextLink;
                continue;
            }

            if (IsAt(text, i, "{") && TryReadToken(text, i + 1, "}", out string? badgeText, out int nextBadge))
            {
                var run = new Run($"[{badgeText.Trim()}]")
                {
                    Foreground = GetAccentBrush() ?? Foreground
                };
                Inlines?.Add(run);
                i = nextBadge;
                continue;
            }

            if (IsAt(text, i, "**") && TryReadToken(text, i + 2, "**", out string? boldText, out int nextBold))
            {
                var bold = new Bold();
                bold.Inlines.Add(new Run(boldText));
                Inlines?.Add(bold);
                i = nextBold;
                continue;
            }

            if (IsAt(text, i, "`") && TryReadToken(text, i + 1, "`", out string? codeText, out int nextCode))
            {
                var run = new Run(codeText)
                {
                    FontFamily = new FontFamily("Menlo,Consolas,monospace")
                };
                Inlines?.Add(run);
                i = nextCode;
                continue;
            }

            if (IsAt(text, i, "*") && TryReadToken(text, i + 1, "*", out string? italicText, out int nextItalic))
            {
                var italic = new Italic();
                italic.Inlines.Add(new Run(italicText));
                Inlines?.Add(italic);
                i = nextItalic;
                continue;
            }

            int next = FindNextSpecial(text, i);
            string chunk = text[i..next];
            if (chunk.Length > 0)
            {
                Inlines?.Add(new Run(chunk));
            }
            i = next;
        }
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

    private static bool TryCreateUri(string raw, out Uri uri)
    {
        uri = default!;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (Uri.TryCreate(raw, UriKind.Absolute, out uri))
            return true;

        return Uri.TryCreate($"https://{raw}", UriKind.Absolute, out uri);
    }

    private static void OpenUrl(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });
        }
        catch
        {
            // Best effort: ignore failures to open browser.
        }
    }
}
