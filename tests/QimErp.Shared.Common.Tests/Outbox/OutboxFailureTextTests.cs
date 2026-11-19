using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QimErp.Shared.Common.Outbox;
using Xunit;

namespace QimErp.Shared.Common.Tests.Outbox;

public sealed class OutboxFailureTextTests
{
    private const string MessageText = "duplicate key value violates unique constraint \"ux_employees_email\"";
    private const string Detail = "Key (email)=(ama.mensah@example.com) already exists.";
    private const string Where = "SQL statement \"INSERT INTO hr.employees\"";
    private const string Hint = "Use a different email.";

    [Fact]
    public void Postgres_failure_reports_sql_state_and_constraint_without_message_or_detail()
    {
        var postgres = new PostgresException(
            MessageText, "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation,
            detail: Detail, hint: Hint, where: Where,
            schemaName: "hr", tableName: "employees", columnName: "email", constraintName: "ux_employees_email");
        var failure = new DbUpdateException("An error occurred while saving the entity changes.", postgres);

        var text = OutboxFailureText.Describe(failure);

        text.Should().Be(
            "Microsoft.EntityFrameworkCore.DbUpdateException: Postgres 23505 constraint=ux_employees_email table=hr.employees column=email");
        text.Should().NotContain("duplicate key")
            .And.NotContain("ama.mensah")
            .And.NotContain("INSERT INTO")
            .And.NotContain(Hint);
    }

    [Fact]
    public void Non_postgres_failure_reports_the_innermost_type_and_message()
    {
        var failure = new InvalidOperationException("outer", new TimeoutException("inner timed out"));

        OutboxFailureText.Describe(failure).Should().Be("System.TimeoutException: inner timed out");
    }

    [Fact]
    public void Single_exception_reports_its_own_type_and_message()
    {
        OutboxFailureText.Describe(new InvalidOperationException("boom"))
            .Should().Be("System.InvalidOperationException: boom");
    }

    [Fact]
    public void Loggable_cause_drops_a_chain_holding_a_postgres_exception()
    {
        var postgres = new PostgresException(MessageText, "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation, detail: Detail);
        var plain = new InvalidOperationException("boom");

        OutboxFailureText.LoggableCause(new DbUpdateException("save failed", postgres)).Should().BeNull();
        OutboxFailureText.LoggableCause(plain).Should().BeSameAs(plain);
    }
}
