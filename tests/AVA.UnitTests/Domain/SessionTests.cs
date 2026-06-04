using AVA.Domain.Entities;
using AVA.Domain.Enums;
using FluentAssertions;

namespace AVA.UnitTests.Domain;

// Unit tests for Session.cs
//
// WHY SESSION NEEDS TESTS:
// Session has real BUSINESS RULES (logic that can break):
//   1. AddMessage() must reject messages from other sessions
//   2. End() must set EndedAt timestamp
//   3. End() must throw if called twice (already ended)
//   4. IsActive must reflect the correct state
//   5. MessageCount must stay accurate
//   6. Messages list must be read-only (outside code cannot modify it)
//
// These are the rules that protect data integrity.
// If any of them break, the conversation history gets corrupted.
public class SessionTests
{
    // Helper: creates a valid Message that belongs to a given session
    private static Message CreateMessage(Guid sessionId, MessageRole role = MessageRole.User)
    {
        return new Message
        {
            SessionId = sessionId,
            Role = role,
            Content = "Test message content",
            DetectedEmotion = EmotionType.Neutral
        };
    }

    // ── Test 1 ────────────────────────────────────────────────────────────
    // A new session should be active (EndedAt is null)
    [Fact]
    public void NewSession_IsActive_ShouldBeTrue()
    {
        var session = new Session();

        session.IsActive.Should().BeTrue("a new session has not been ended yet");
        session.EndedAt.Should().BeNull("EndedAt is only set when End() is called");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────
    // Adding a valid message should increase MessageCount
    [Fact]
    public void AddMessage_ValidMessage_IncreasesMessageCount()
    {
        var session = new Session();
        var message = CreateMessage(session.Id);

        session.AddMessage(message);

        session.MessageCount.Should().Be(1);
        session.Messages.Should().ContainSingle()
            .Which.Content.Should().Be("Test message content");
    }

    // ── Test 3 ────────────────────────────────────────────────────────────
    // Adding multiple messages - count must stay accurate
    [Fact]
    public void AddMessage_MultipleMessages_CountIsAccurate()
    {
        var session = new Session();

        session.AddMessage(CreateMessage(session.Id, MessageRole.User));
        session.AddMessage(CreateMessage(session.Id, MessageRole.Assistant));
        session.AddMessage(CreateMessage(session.Id, MessageRole.User));

        session.MessageCount.Should().Be(3);
        session.Messages.Should().HaveCount(3);
    }

    // ── Test 4 ────────────────────────────────────────────────────────────
    // CRITICAL: A message from a DIFFERENT session must be rejected
    // This is the main business rule that protects data integrity
    [Fact]
    public void AddMessage_MessageFromDifferentSession_ThrowsInvalidOperationException()
    {
        var session = new Session();
        var wrongSessionId = Guid.NewGuid(); // Different ID!
        var foreignMessage = CreateMessage(wrongSessionId);

        // FluentAssertions: .Invoking(...).Should().Throw<T>()
        // This is how you test that an exception IS thrown
        var act = () => session.AddMessage(foreignMessage);

        act.Should().Throw<InvalidOperationException>(
            "a message from a different session must never be added")
           .WithMessage("*" + wrongSessionId + "*",
            "the error message should mention which session the message belonged to");
    }

    // ── Test 5 ────────────────────────────────────────────────────────────
    // End() should set EndedAt and flip IsActive to false
    [Fact]
    public void End_ActiveSession_SetsEndedAtAndIsActiveFalse()
    {
        var session = new Session();
        var beforeEnd = DateTime.UtcNow;

        session.End();

        session.IsActive.Should().BeFalse("ended session should not be active");
        session.EndedAt.Should().NotBeNull("EndedAt must be set when End() is called");
        session.EndedAt.Should().BeOnOrAfter(beforeEnd,
            "EndedAt should be set to the current UTC time");
    }

    // ── Test 6 ────────────────────────────────────────────────────────────
    // CRITICAL: Calling End() twice must throw - prevents double-ending
    [Fact]
    public void End_AlreadyEndedSession_ThrowsInvalidOperationException()
    {
        var session = new Session();
        session.End(); // First call - valid

        // Second call - must throw
        var act = () => session.End();

        act.Should().Throw<InvalidOperationException>(
            "ending an already-ended session is a bug and must be caught");
    }

    // ── Test 7 ────────────────────────────────────────────────────────────
    // Messages property must return IReadOnlyList - outside code cannot add
    [Fact]
    public void Messages_Property_IsReadOnly()
    {
        var session = new Session();

        // The compile-time type of Messages must be IReadOnlyList<Message>
        // This test verifies the interface contract is honoured
        session.Messages.Should().BeAssignableTo<IReadOnlyList<Message>>(
            "Messages must be read-only to prevent external mutation");
    }

    // ── Test 8 ────────────────────────────────────────────────────────────
    // New session starts with zero messages
    [Fact]
    public void NewSession_Messages_IsEmpty()
    {
        var session = new Session();

        session.Messages.Should().BeEmpty("a new session has no messages yet");
        session.MessageCount.Should().Be(0);
    }

    // ── Test 9 ────────────────────────────────────────────────────────────
    // Session ID must be unique - two sessions should never share an ID
    [Fact]
    public void TwoSessions_AlwaysHaveDifferentIds()
    {
        var session1 = new Session();
        var session2 = new Session();

        session1.Id.Should().NotBe(session2.Id,
            "each session must have a globally unique ID");
    }

    // ── Test 10 ───────────────────────────────────────────────────────────
    // Adding a message to an ended session - should this be allowed?
    // Decision: We allow it (no validation in AddMessage for IsActive).
    // The session is read-only after ended, but messages can still be added
    // to support late-arriving async responses.
    [Fact]
    public void AddMessage_ToEndedSession_DoesNotThrow()
    {
        var session = new Session();
        session.End();
        var message = CreateMessage(session.Id);

        var act = () => session.AddMessage(message);

        act.Should().NotThrow(
            "adding a message to an ended session is allowed for async pipeline completion");
    }
}
