using Condux.Core.Issues;
using Xunit;

namespace Condux.Core.Tests;

public sealed class IssueQueryTests
{
    [Fact]
    public void Empty_query_filters_nothing()
    {
        var filter = IssueQuery.Parse("");
        Assert.Null(filter.Text);
        Assert.Null(filter.Level);
        Assert.Null(filter.Status);
        Assert.Null(filter.Assigned);
        Assert.False(filter.AssignedToMe);
    }

    [Fact]
    public void Parses_the_known_tokens()
    {
        var filter = IssueQuery.Parse("level:error is:unresolved assigned:me boom");
        Assert.Equal(4, filter.Level); // error
        Assert.Equal(1, filter.Status); // unresolved
        Assert.True(filter.AssignedToMe);
        Assert.Equal("boom", filter.Text);
    }

    [Fact]
    public void Is_assigned_and_unassigned_set_the_assigned_flag()
    {
        Assert.True(IssueQuery.Parse("is:assigned").Assigned);
        Assert.False(IssueQuery.Parse("is:unassigned").Assigned);
        Assert.Null(IssueQuery.Parse("boom").Assigned);
    }

    [Fact]
    public void Unknown_tokens_and_bad_values_fall_back_to_free_text()
    {
        var filter = IssueQuery.Parse("level:nope tag:x plain words");
        Assert.Null(filter.Level); // "nope" is not a level → not a filter
        Assert.Equal("level:nope tag:x plain words", filter.Text);
    }
}
