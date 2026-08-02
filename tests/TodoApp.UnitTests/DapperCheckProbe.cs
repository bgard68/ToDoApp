// Temporary: proves Build & Test now runs on a dapper pull request, and fails it.
public class DapperCheckProbe
{
    [Xunit.Fact]
    public void This_must_fail_and_must_be_seen() => Xunit.Assert.Equal(1, 2);
}
