using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using AvaloniaEdit;

namespace AxmolHub;

/// <summary>The one way Hub puts a file into an editor: the palette the transcript's code blocks are drawn with, a
/// line-number gutter, and no way to type into it. A file opened straight from disk is a read, and a reader that
/// can lose keystrokes is the beginning of a second editor nobody asked for.</summary>
internal static class CodeSurface
{
    /// <summary>The font a project file is read in. One name for both the gutter and the text, because a gutter
    /// in a different family than the code it numbers drifts by a pixel a line.</summary>
    internal static readonly FontFamily Mono = FontFamily.Parse("Menlo,Consolas,monospace");

    internal static TextEditor ForFile(string? path, string text)
    {
        var editor = new TextEditor
        {
            Text = text,
            IsReadOnly = true,
            WordWrap = false,
            ShowLineNumbers = true,
            FontFamily = Mono,
            FontSize = 13,
        };
        // AvaloniaEdit's theme rides on a style include rather than the application's, and an editor without it
        // draws a white page under a palette made for a dark well.
        editor.Styles.Add(new StyleInclude(new Uri("avares://AxmolHub/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });
        editor.Bind(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("Hub.TextBody"));
        editor.Bind(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("Hub.SurfaceSunken"));
        // The file name, not the fence tag: this is the caller that makes <see cref="CodeSyntax.ForFile"/> and the
        // transcript's blocks one answer instead of two.
        editor.SyntaxHighlighting = DarkSyntax.ForBackdrop(CodeSyntax.ForFile(path));
        return editor;
    }
}
