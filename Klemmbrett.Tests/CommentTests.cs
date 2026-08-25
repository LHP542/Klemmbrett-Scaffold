using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Klemmbrett.Models;
using Klemmbrett.Services;
using Xunit;

namespace Klemmbrett.Tests;

public class CommentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kb-comment-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Comment_SurvivesStorageRoundtrip()
    {
        var storage = new HistoryStorageService(_dir, new TestProtector());
        storage.SaveIndex([
            new TextClipboardEntry("mit-notiz") { Comment = "Zugang für Kunde X" }
        ]);

        var loaded = new HistoryStorageService(_dir, new TestProtector()).Load();

        var entry = loaded.OfType<TextClipboardEntry>().Single();
        entry.Comment.Should().Be("Zugang für Kunde X");
        entry.HasComment.Should().BeTrue();
    }

    [Fact]
    public void Entry_WithoutComment_HasNoComment()
    {
        var entry = new TextClipboardEntry("ohne");
        entry.HasComment.Should().BeFalse();
        entry.Comment.Should().BeNull();
    }

    [Fact]
    public void OldIndex_WithoutCommentField_LoadsWithNullComment()
    {
        // Simuliert eine vor der Kommentar-Funktion geschriebene history.json.
        //
        // Der Zeitstempel MUSS relativ zu heute sein. HistoryStorageService.Load
        // wirft ungepinnte Eintraege weg, die aelter als
        // HistoryDayFilter.RetentionDays (30) sind — ein festes Datum im
        // Testtext macht daraus eine Zeitbombe, die ohne jede Codeaenderung rot
        // wird, sobald genug Zeit vergangen ist. Genau das ist hier passiert:
        // mit dem urspruenglichen 2026-07-23 lief der Test bis zum 30. Tag
        // gruen und danach auf "Sequence contains no elements" — der Eintrag
        // war schlicht abgelaufen, nicht kaputt gelesen.
        //
        // Geprueft werden soll die SCHEMA-Vertraeglichkeit (fehlendes
        // Comment-Feld), nicht die Aufbewahrungsfrist.
        Directory.CreateDirectory(_dir);
        var gestern = DateTimeOffset.Now.AddDays(-1).ToString("o");
        File.WriteAllText(Path.Combine(_dir, "history.json"),
            $$"""[{"Type":"text","CapturedAt":"{{gestern}}","Text":"alt","Hash":null,"Pinned":false}]""");

        var loaded = new HistoryStorageService(_dir, new TestProtector()).Load();

        loaded.OfType<TextClipboardEntry>().Single().Comment.Should().BeNull();
    }
}
