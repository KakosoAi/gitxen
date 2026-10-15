// SPDX-License-Identifier: GPL-3.0-or-later
using AwesomeAssertions;
using Gitxen.Terminal;
using NUnit.Framework;

namespace Gitxen.Tests;

[TestFixture]
public sealed class TerminalOutputBufferTests
{
    private static string Render(params string[] chunks)
    {
        TerminalOutputBuffer buffer = new();

        foreach (string chunk in chunks)
        {
            buffer.Append(chunk);
        }

        return buffer.GetText();
    }

    [Test]
    public void Append_should_keep_plain_lines()
    {
        Render("one\r\ntwo\r\n").Should().Be("one\ntwo\n");
    }

    [Test]
    public void Append_should_overwrite_on_carriage_return_like_a_progress_bar()
    {
        Render("progress 10%\rprogress 99%\r\ndone").Should().Be("progress 99%\ndone");
    }

    [Test]
    public void Append_should_drop_colours_and_window_titles()
    {
        Render("\u001b]0;title\u0007\u001b[32mgreen\u001b[0m text").Should().Be("green text");
    }

    [Test]
    public void Append_should_handle_an_escape_sequence_split_across_reads()
    {
        Render("red \u001b[3", "1mword\u001b", "[0m!").Should().Be("red word!");
    }

    [Test]
    public void Append_should_clear_on_erase_in_display()
    {
        Render("old output\r\n", "\u001b[2J\u001b[H", "fresh").Should().Be("fresh");
    }

    [Test]
    public void Append_should_truncate_on_erase_in_line()
    {
        Render("abcdef\r", "xy\u001b[K").Should().Be("xy");
    }

    [Test]
    public void Append_should_step_back_on_backspace()
    {
        Render("cat\br").Should().Be("car");
    }

    [Test]
    public void Append_should_expand_tabs_to_the_next_stop()
    {
        Render("a\tb").Should().Be("a       b");
    }

    [Test]
    public void Append_should_treat_moving_down_a_row_as_a_new_line()
    {
        Render("first", "\u001b[2;1H", "second").Should().Be("first\nsecond");
    }

    [Test]
    public void GetText_should_trim_trailing_spaces_and_cap_blank_runs()
    {
        Render("prompt   \r\n\r\n\r\n\r\n\r\nnext").Should().Be("prompt\n\n\nnext");
    }

    [Test]
    public void Clear_should_empty_the_transcript()
    {
        TerminalOutputBuffer buffer = new();
        buffer.Append("something\r\nelse");

        buffer.Clear();

        buffer.GetText().Should().BeEmpty();
    }

    [Test]
    public void Append_should_keep_only_the_scrollback_cap()
    {
        TerminalOutputBuffer buffer = new();

        for (int i = 0; i < 2500; i++)
        {
            buffer.Append($"line {i}\r\n");
        }

        string text = buffer.GetText();
        text.Should().NotContain("line 0\n");
        text.Should().Contain("line 2499");
        text.Split('\n').Length.Should().Be(2000);
    }
}
