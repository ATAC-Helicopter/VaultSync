using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace VaultSync.UI.Views.Controls;

public class RichTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> RichTextProperty =
        AvaloniaProperty.Register<RichTextBlock, string?>(nameof(RichText));

    public string? RichText
    {
        get => GetValue(RichTextProperty);
        set => SetValue(RichTextProperty, value);
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
        if (string.IsNullOrEmpty(text))
            return;

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\n')
            {
                Inlines?.Add(new LineBreak());
                i++;
                continue;
            }

            if (IsAt(text, i, "~~") && TryReadToken(text, i + 2, "~~", out var strikeText, out var nextStrike))
            {
                var run = new Run(strikeText)
                {
                    TextDecorations = CreateStrikethrough()
                };
                Inlines?.Add(run);
                i = nextStrike;
                continue;
            }

            if (IsAt(text, i, "[") && TryReadLink(text, i, out var linkText, out var linkUrl, out var nextLink))
            {
                if (TryCreateUri(linkUrl, out var uri))
                {
                    var linkBlock = new TextBlock
                    {
                        Text = linkText,
                        Foreground = GetAccentBrush() ?? Foreground,
                        TextDecorations = CreateUnderline(),
                        Cursor = new Cursor(StandardCursorType.Hand)
                    };
                    linkBlock.PointerPressed += (_, e) =>
                    {
                        if (e.GetCurrentPoint(linkBlock).Properties.IsLeftButtonPressed)
                            OpenUrl(uri);
                    };
                    Inlines?.Add(new InlineUIContainer { Child = linkBlock });
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

            if (IsAt(text, i, "{") && TryReadToken(text, i + 1, "}", out var badgeText, out var nextBadge))
            {
                var run = new Run($"[{badgeText.Trim()}]")
                {
                    Foreground = GetAccentBrush() ?? Foreground
                };
                Inlines?.Add(run);
                i = nextBadge;
                continue;
            }

            if (IsAt(text, i, "**") && TryReadToken(text, i + 2, "**", out var boldText, out var nextBold))
            {
                var bold = new Bold();
                bold.Inlines.Add(new Run(boldText));
                Inlines?.Add(bold);
                i = nextBold;
                continue;
            }

            if (IsAt(text, i, "`") && TryReadToken(text, i + 1, "`", out var codeText, out var nextCode))
            {
                var run = new Run(codeText)
                {
                    FontFamily = new FontFamily("Menlo,Consolas,monospace")
                };
                Inlines?.Add(run);
                i = nextCode;
                continue;
            }

            if (IsAt(text, i, "*") && TryReadToken(text, i + 1, "*", out var italicText, out var nextItalic))
            {
                var italic = new Italic();
                italic.Inlines.Add(new Run(italicText));
                Inlines?.Add(italic);
                i = nextItalic;
                continue;
            }

            var next = FindNextSpecial(text, i);
            var chunk = text.Substring(i, next - i);
            if (chunk.Length > 0)
            {
                Inlines?.Add(new Run(chunk));
            }
            i = next;
        }
    }

    private static bool IsAt(string value, int index, string token)
    {
        if (index + token.Length > value.Length)
            return false;
        return string.Compare(value, index, token, 0, token.Length, StringComparison.Ordinal) == 0;
    }

    private static bool TryReadToken(string value, int start, string token, out string content, out int nextIndex)
    {
        var end = value.IndexOf(token, start, StringComparison.Ordinal);
        if (end < 0)
        {
            content = string.Empty;
            nextIndex = start;
            return false;
        }

        content = value.Substring(start, end - start);
        nextIndex = end + token.Length;
        return true;
    }

    private static int FindNextSpecial(string value, int start)
    {
        var next = value.Length;
        var newline = value.IndexOf('\n', start);
        if (newline >= 0)
            next = Math.Min(next, newline);
        var bold = value.IndexOf("**", start, StringComparison.Ordinal);
        if (bold >= 0)
            next = Math.Min(next, bold);
        var strike = value.IndexOf("~~", start, StringComparison.Ordinal);
        if (strike >= 0)
            next = Math.Min(next, strike);
        var italic = value.IndexOf('*', start);
        if (italic >= 0)
            next = Math.Min(next, italic);
        var code = value.IndexOf('`', start);
        if (code >= 0)
            next = Math.Min(next, code);
        var link = value.IndexOf('[', start);
        if (link >= 0)
            next = Math.Min(next, link);
        var badge = value.IndexOf('{', start);
        if (badge >= 0)
            next = Math.Min(next, badge);
        return next;
    }

    private static bool TryReadLink(string value, int start, out string text, out string url, out int nextIndex)
    {
        text = string.Empty;
        url = string.Empty;
        nextIndex = start;

        var closeBracket = value.IndexOf(']', start + 1);
        if (closeBracket < 0)
            return false;
        if (closeBracket + 1 >= value.Length || value[closeBracket + 1] != '(')
            return false;
        var closeParen = value.IndexOf(')', closeBracket + 2);
        if (closeParen < 0)
            return false;

        var label = value.Substring(start + 1, closeBracket - start - 1);
        var link = value.Substring(closeBracket + 2, closeParen - closeBracket - 2);
        text = string.IsNullOrWhiteSpace(label) ? link : label;
        url = link;
        nextIndex = closeParen + 1;
        return true;
    }

    private IBrush? GetAccentBrush()
    {
        if (Application.Current is not null &&
            Application.Current.TryFindResource("AccentBrush", out var value))
        {
            return value as IBrush;
        }

        return null;
    }

    private static TextDecorationCollection CreateUnderline()
        => new() { new TextDecoration { Location = TextDecorationLocation.Underline } };

    private static TextDecorationCollection CreateStrikethrough()
        => new() { new TextDecoration { Location = TextDecorationLocation.Strikethrough } };

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
