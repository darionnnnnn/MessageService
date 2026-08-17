using MessageService.Services;

namespace MessageService.Tests.Services;

public class ProcessOwnerIdTests
{
    [Fact]
    public void ProcessOwnerId_SameSiteKey_ProducesSameValue()
    {
        var owner1 = new ProcessOwnerId("C:\\inetpub\\wwwroot\\site1");
        var owner2 = new ProcessOwnerId("C:\\inetpub\\wwwroot\\site1");

        Assert.Equal(owner1.Value, owner2.Value);
        Assert.Equal(owner1.ToString(), owner1.Value);

        // 預設站台鍵（AppContext.BaseDirectory）的兩實例也必須相等且與 Instance 一致
        var default1 = new ProcessOwnerId();
        var default2 = new ProcessOwnerId();
        Assert.Equal(default1.Value, default2.Value);
        Assert.Equal(ProcessOwnerId.Instance.Value, default1.Value);
    }

    [Fact]
    public void ProcessOwnerId_DifferentSiteKey_ProducesDifferentValue()
    {
        var owner1 = new ProcessOwnerId("C:\\inetpub\\wwwroot\\site1");
        var owner2 = new ProcessOwnerId("C:\\inetpub\\wwwroot\\site2");

        Assert.NotEqual(owner1.Value, owner2.Value);
    }

    [Fact]
    public void ProcessOwnerId_Value_StartsWithMachineNameAndLengthNotExceed128()
    {
        var owner = new ProcessOwnerId("C:\\inetpub\\wwwroot\\site1");
        var machine = Environment.MachineName;

        Assert.StartsWith($"{machine}-", owner.Value);
        Assert.True(owner.Value.Length <= 128);

        // 驗證字尾為 8 碼十六進位
        var suffix = owner.Value[(machine.Length + 1)..];
        Assert.Equal(8, suffix.Length);
        Assert.Matches("^[0-9a-fA-F]{8}$", suffix);

        // 驗證極長站台鍵情況下長度亦不超過 128
        var longKeyOwner = new ProcessOwnerId(new string('x', 1000));
        Assert.True(longKeyOwner.Value.Length <= 128);
    }
}
