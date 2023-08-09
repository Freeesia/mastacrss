namespace mastacrss_test;

public class ProfileInfoTest
{
    [Fact]
    public async Task FetchProfileTest()
    {
        {
            var profile = await ProfileInfo.FetchFromWebsite(new Uri("https://www.youtube.com/channel/UCQ0UDLQCjY0rmuxCDE38FGg"));
            Assert.Equal("youtube_NatsuiroMatsuri", profile.Name);
            Assert.Equal("Matsuri Channel 夏色まつり", profile.Title);
            Assert.Equal("http://www.youtube.com/@NatsuiroMatsuri", profile.Link);
            Assert.Equal("http://www.youtube.com/feeds/videos.xml?channel_id=UCQ0UDLQCjY0rmuxCDE38FGg", profile.Rss);
        }
        {
            var profile = await ProfileInfo.FetchFromWebsite(new Uri("http://www.youtube.com/@NatsuiroMatsuri"));
            Assert.Equal("youtube_NatsuiroMatsuri", profile.Name);
            Assert.Equal("Matsuri Channel 夏色まつり", profile.Title);
            Assert.Equal("http://www.youtube.com/@NatsuiroMatsuri", profile.Link);
            Assert.Equal("http://www.youtube.com/feeds/videos.xml?channel_id=UCQ0UDLQCjY0rmuxCDE38FGg", profile.Rss);
        }
        {
            var profile = await ProfileInfo.FetchFromWebsite(new Uri("https://ufcpp.net/rssblog"));
            Assert.Equal("ufcpp_blog", profile.Name);
            Assert.Equal("++C++; // 未確認飛行 C ブログ", profile.Title);
            Assert.Equal("http://ufcpp.net/blog/", profile.Link);
            Assert.Equal("https://ufcpp.net/rssblog", profile.Rss);
        }
    }

    [Fact]
    public async Task FetchProfileTest_InvalidUrl()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ProfileInfo.FetchFromWebsite(new Uri("https://www.google.com")));
    }

    [Fact]
    public async Task FetchProfileTest_NonExistentUrl()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ProfileInfo.FetchFromWebsite(new Uri("https://www.youtube.com/channel/invalid_channel_id")));
    }
}