// Temporary: proves the required-status-checks rule actually blocks a red PR.
public class RequiredCheckProbe
{
    [Xunit.Fact]
    public void This_must_fail_and_must_block_the_merge() => Xunit.Assert.Equal(1, 2);
}
