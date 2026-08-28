using MealiePicnic.Storage;

namespace MealiePicnic.Tests;

public class HouseholdKeyTests
{
    [Fact]
    public void Same_household_id_produces_the_same_key()
    {
        var id = Guid.NewGuid();

        Assert.Equal(HouseholdKey.From(id), HouseholdKey.From(id));
    }

    [Fact]
    public void Different_household_ids_produce_different_keys()
    {
        Assert.NotEqual(HouseholdKey.From(Guid.NewGuid()), HouseholdKey.From(Guid.NewGuid()));
    }

    [Fact]
    public void Key_is_filesystem_safe()
    {
        var key = HouseholdKey.From(Guid.NewGuid());

        Assert.Matches("^[a-f0-9]{16}$", key);
    }

    [Fact]
    public void Local_is_the_fixed_pseudo_household()
    {
        Assert.Equal("local", HouseholdKey.Local);
    }
}
