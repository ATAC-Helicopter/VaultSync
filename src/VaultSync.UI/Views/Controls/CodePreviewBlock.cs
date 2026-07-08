using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace VaultSync.UI.Views.Controls;

public sealed class CodePreviewBlock : TextBlock
{
    private const int MaxHighlightedLines = 6000;
    private static readonly IBrush LineNumberBrush = Brush.Parse("#7E8EA3");
    private static readonly IBrush KeywordBrush = Brush.Parse("#7CB7FF");
    private static readonly IBrush StringBrush = Brush.Parse("#9FE6A0");
    private static readonly IBrush NumberBrush = Brush.Parse("#F5C16C");
    private static readonly IBrush CommentBrush = Brush.Parse("#7E8EA3");
    private static readonly IBrush PunctuationBrush = Brush.Parse("#B8C7D9");
    private static readonly IBrush MarkupBrush = Brush.Parse("#FF9AA2");

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "async", "await", "base", "bool", "break", "case", "catch", "class", "const",
        "continue", "default", "delegate", "do", "double", "else", "enum", "event", "false",
        "finally", "float", "for", "foreach", "if", "in", "int", "interface", "internal", "is",
        "lock", "long", "namespace", "new", "null", "object", "out", "override", "private",
        "protected", "public", "readonly", "record", "return", "sealed", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "using", "var", "void", "while",
        "let", "const", "function", "import", "export", "from", "type", "interface", "extends",
        "implements", "package", "def", "elif", "None", "True", "False", "yield"
    };

    public static readonly StyledProperty<string?> CodeTextProperty =
        AvaloniaProperty.Register<CodePreviewBlock, string?>(nameof(CodeText));

    public static readonly StyledProperty<string?> FileNameProperty =
        AvaloniaProperty.Register<CodePreviewBlock, string?>(nameof(FileName));

    public string? CodeText
    {
        get => GetValue(CodeTextProperty);
        set => SetValue(CodeTextProperty, value);
    }

    public string? FileName
    {
        get => GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    public CodePreviewBlock()
    {
        FontFamily = new FontFamily("Menlo, Consolas, Monaco, monospace");
        FontSize = 12;
        TextWrapping = TextWrapping.NoWrap;
        LineHeight = 18;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CodeTextProperty || change.Property == FileNameProperty)
            UpdateInlines();
    }

    private void UpdateInlines()
    {
        Inlines?.Clear();
        string text = CodeText ?? string.Empty;
        if (text.Length == 0)
            return;

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        int displayLines = Math.Min(lines.Length, MaxHighlightedLines);
        int numberWidth = displayLines.ToString(CultureInfo.InvariantCulture).Length;

        for (int i = 0; i < displayLines; i++)
        {
            AddLineNumber(i + 1, numberWidth);
            AddHighlightedLine(lines[i]);
            if (i < displayLines - 1)
                Inlines?.Add(new LineBreak());
        }

        if (lines.Length > MaxHighlightedLines)
        {
            Inlines?.Add(new LineBreak());
            Inlines?.Add(new Run($"… {lines.Length - MaxHighlightedLines} more line(s) hidden in preview")
            {
                Foreground = CommentBrush
            });
        }
    }

    private void AddLineNumber(int lineNumber, int numberWidth)
    {
        Inlines?.Add(new Run(lineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(numberWidth) + "  ")
        {
            Foreground = LineNumberBrush
        });
    }

    private void AddHighlightedLine(string line)
    {
        if (line.Length == 0)
            return;

        if (IsWholeLineComment(line))
        {
            AddRun(line, CommentBrush);
            return;
        }

        int i = 0;
        while (i < line.Length)
        {
            if (TryAddStringToken(line, ref i) ||
                TryAddLineComment(line, ref i) ||
                TryAddNumberToken(line, ref i) ||
                TryAddIdentifierToken(line, ref i) ||
                TryAddPunctuationToken(line, ref i))
            {
                continue;
            }

            AddRun(line[i].ToString(), Foreground);
            i++;
        }
    }

    private static bool IsWholeLineComment(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("#", StringComparison.Ordinal) ||
            trimmed.StartsWith("<!--", StringComparison.Ordinal) ||
            trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    private bool TryAddStringToken(string line, ref int index)
    {
        char c = line[index];
        if (c is not ('"' or '\''))
            return false;

        int end = FindStringEnd(line, index, c);
        AddRun(line[index..end], StringBrush);
        index = end;
        return true;
    }

    private bool TryAddLineComment(string line, ref int index)
    {
        if (line[index] != '/' || index + 1 >= line.Length || line[index + 1] != '/')
            return false;

        AddRun(line[index..], CommentBrush);
        index = line.Length;
        return true;
    }

    private bool TryAddNumberToken(string line, ref int index)
    {
        if (!char.IsDigit(line[index]))
            return false;

        int end = index + 1;
        while (end < line.Length && IsNumberPart(line[end]))
            end++;

        AddRun(line[index..end], NumberBrush);
        index = end;
        return true;
    }

    private bool TryAddIdentifierToken(string line, ref int index)
    {
        if (!IsIdentifierStart(line[index]))
            return false;

        int end = index + 1;
        while (end < line.Length && IsIdentifierPart(line[end]))
            end++;

        string token = line[index..end];
        AddRun(token, Keywords.Contains(token) ? KeywordBrush : Foreground);
        index = end;
        return true;
    }

    private bool TryAddPunctuationToken(string line, ref int index)
    {
        char c = line[index];
        if (!"{}[]()<>/=:;,.+-*!?|&%@".Contains(c, StringComparison.Ordinal))
            return false;

        AddRun(c.ToString(), c is '<' or '>' or '/' ? MarkupBrush : PunctuationBrush);
        index++;
        return true;
    }

    private void AddRun(string text, IBrush? brush)
    {
        if (text.Length == 0)
            return;

        Inlines?.Add(new Run(text)
        {
            Foreground = brush ?? Foreground
        });
    }

    private static int FindStringEnd(string line, int start, char delimiter)
    {
        int i = start + 1;
        bool escaped = false;
        while (i < line.Length)
        {
            char c = line[i++];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == delimiter)
                break;
        }

        return i;
    }

    private static bool IsIdentifierStart(char value) =>
        char.IsLetter(value) || value == '_';

    private static bool IsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_' || value == '-';

    private static bool IsNumberPart(char value) =>
        char.IsDigit(value) || value is '.' or '_' or 'x' or 'X' || IsHexLetter(value);

    private static bool IsHexLetter(char value) =>
        value is >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
