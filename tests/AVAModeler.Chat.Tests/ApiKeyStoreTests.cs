namespace AVAModeler.Chat.Tests;

public class ApiKeyStoreTests
{
    // Made-up keys: the tests never read the real environment variable or the real key file.
    private const string Saved = "sk-ant-api03-made-up-saved-key-1234";
    private const string FromEnvironment = "sk-ant-api03-made-up-environment-5678";

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "avamodeler-chat-" + Guid.NewGuid().ToString("N"), ApiKeyStore.FileName);

    [Fact]
    public void A_saved_key_is_kept_owner_only_and_the_environment_wins()
    {
        var path = TempFile();
        string? env = null;
        var store = new ApiKeyStore(path, () => env);
        try
        {
            Assert.Null(store.Get());
            Assert.Equal(KeySource.None, store.Source);

            store.Save("  " + Saved + "\n");
            Assert.Equal(Saved, File.ReadAllText(path));
            Assert.Equal(Saved, store.Get());
            Assert.Equal(KeySource.File, store.Source);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            // Saving again replaces it, still owner-only.
            store.Save(Saved + "9");
            Assert.Equal(Saved + "9", new ApiKeyStore(path, null).Get());
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            env = FromEnvironment;
            Assert.Equal(FromEnvironment, store.Get());
            Assert.Equal(KeySource.Environment, store.Source);

            env = null;
            store.Remove();
            Assert.False(File.Exists(path));
            Assert.Null(store.Get());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void Only_a_masked_key_is_ever_shown()
    {
        var masked = ApiKeyStore.Mask(Saved);
        Assert.Equal("sk-ant-…1234", masked);
        Assert.DoesNotContain("made-up", masked);
        Assert.Equal("••••", ApiKeyStore.Mask("short"));
        Assert.Equal("none", ApiKeyStore.Mask(null));
        Assert.Throws<ArgumentException>(() => new ApiKeyStore(null, null).Save("   "));
    }

    [Fact]
    public void A_store_without_a_file_keeps_the_key_for_the_run_only()
    {
        var store = new ApiKeyStore(null, null);
        store.Save(Saved);
        Assert.Equal(Saved, store.Get());
        store.Remove();
        Assert.Null(store.Get());
    }
}
