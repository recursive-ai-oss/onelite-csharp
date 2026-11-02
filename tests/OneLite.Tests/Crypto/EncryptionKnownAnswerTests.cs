using System;
using RecursiveAI.OneLite.Storage.Crypto;

namespace RecursiveAI.OneLite.Tests.Crypto;

/// <summary>
/// Known-answer pages built with OpenSSL and Python's standard library from Zetetic's published design (tools/scripts/gen_cipher_kat.py), so the
/// codec is checked against primitives that share no code with it. Zetetic publishes no numeric test vectors; these come from its prose design.
/// </summary>
public class EncryptionKnownAnswerTests
{
    private const string V4_page_2 =
        "11c585580ee7bef06c83ce357878a66e6d2a390a3896776282484b42fc65a305909e017e304b9364b78e6283b03a8065fcc7124a8b8221e5a41ff6179f4a768c751aa3c6f78bb20ce0959dbfdf58a87fc06dc338786c6b0eacac0b906d85c2acbb148856236f222655d0ab824979cc57655405800728cad5102857f993b72f1ad178f661eecbab27d50bd93a8bf14c560590328fe474912a7be1e64f6fba9a3107c1669f45ba8fa34fca79c3bdc53b1780dbc73bd656d09b2751ef374f0e7393f2c9508f9e9b930ae3998858443c3d9738618ae7af8d50834cbae5fdddfd4b67d0ed0feba2b95a24effee353a1f8ab4e15ef2f3309325134b8b4fc8ad9f4096c2f355d6c7cfe1f31b34071c7874065c5626c2d76e3d1fd5fbe8acbf921c490052699d3f131e75351f0d713520b7ef28ce741c6028910c73ca154df0278405c8be093503a680216250c193937d050bd8113a0ae16e4e5b698474f9e97a36e6f26fc53a2d409a4afa8e7289b25cc0071aa05f128c91db213761a0c0779119be7ce37850c91e442c214e24ae648b71bb5325baecc5248e312efb78effa9f06f6276b68c6176ec494df747019c01d70db5e9597a1a182a1d384b9b00eaf63f17f5223433be067f495ab578c6d7b87603453bf0ec69f660ec47036c93a83b3e23e9d000ed0c84a46a0f6a9ee8115cfe29d05c07e224f9f0d14ba1d56a57fcd4efaff46a1de69bb56f266c9052f0c3d913ea2ccea982044f2dc6f3f12d8b064645615e0bd3dbc5bcb926e539b2eab9a43f39de837f697b17800ccb2f3e1eb96f5ab2095feed2210211aefac3773f0dd6dbcec6521328062d1baa3b5836f662a553ec46138b80a03976a42ff7fc85ea9a0680e8867ffdbe55500bb7c0a4d666b2babe61b551130c083bf61084e4e343d9f0ed380e2b1fc47188ffc8e8707075d458e91be3e6890b845d5729014764e626a4c5b595208811cc5d7d317bd6d5ac1cc0305a68728919788b935dc08c5fb43ce1fe0fce6ad915388c46fb918aaa7d636b12c45bcc3b22a631f1881066a62fa74bb68ed0c7765625af2eebf887e095b992e73192b06d012e0506a15c9021bd133050106897dc258a0fcf90aa5b44d616d9c767afe8681a7d1eded9706231825103f3d1d07c6e0e389d69cc62dc87b42c0564c25978dd4f0f616980d753e05a2b6a65ad24d678a8c19545520ac97be92402e3e9376c8bb2c474bdbf3fd00394f0f32476720040794aaaeb982b5e787e157be834ec63787307aac10b79b069038224bc4c85a905108c5689226b48117e5ccdd461ccd0db43e85f4033fd8a045c52b7adc1101112131415161718191a1b1c1d1e1fc4612c8d83647fb12058f5acdf65a44ba3fcff55079d419ec4b522205c33c31f1eeb578262e2e97181761853d655273bd8967230414bc470f23257561fd79cea";
    private const string V4_page_1 =
        "a0a1a2a3a4a5a6a7a8a9aaabacadaeaf4df0ed74761371cabec59316ec18b67a26f5757349bfdbf26d6dd37255de0c0ce89f9122c239a24c7e454dbc56a6146865531a6bc6c3d0d8d9d5b685f7fda39f117f808d787b469ca7cc83da9b7b4ef1d394f2ef457ee17aecb73ed0f0749cb0df8872b7e1bd202f5354f1355b34782413cfd9a9cfc5252a7ede122a352f659dded7bc5fe2b9efa87c47a883031ab8a68f932060c1f1da5d4ccdd07d058a933f86457185fb66a5a68fcf9c62c4979909618a8205fee0cb5e54794ced5e1aaf87336791f5567d9beeed407f5e788a2226884e466fb5d48d7b160f840f01e0586c3a384dc92c5d0d76e58ef613bb981ce101fa819569504cc0321cd278167fcba5af01e8502315f6cdeb77a5c8e6c34473567dd440202491e530021a3c209c07d8e502b5f6d966a081e76fdda24c96c458624cb50edf616603c8bbe63ce1e1d16f3fe9d774b94be04c240f7207419de023da99193fba7d6dfd13aca7cfc862c933086ddfaa4d031d56e1113f60141a5ed613d732c91e39962c135f2d1221a0dfbf021821f6c367dbce92cb638cd85aeddc48711b69a626ce3645a32ec6d9b2a532b33862ecb002353e0d643a14f4e22372c78b2a5b6ebdf802c98bd231fe63c585e1e0928005bc60a11123d15f2e2465c1bc3e023ec8a240df3ceba8d8d3acb19a368ad92443c44fc792f429d93714f61553b6c497cf222d965457801734e1c8db20a8886f2542c23c415b8e13a2ab61d4b5f7ec1143e86f383d57f5e7d56d3132294ae36bcc53cd0a1b98c78926baa87abb67a884e2c4c0e225d008cb0f3c331665d895ff87940c50ab307c188a2f9450fbb76bcb6be1de76dc5b325a37d35b7f453fea637b648c797bb86e9f6684ba2e4bcba123877a61488da4bb0b4ca124f89ebed60f18a3f368916748d541ece7725b001d143dbb1d744ad76c4d8b91169d4f7d137af8007ab3d9a90320bc6d8f7d14ea8409cb934203bf59ecc7ca90d51c15d969eb27798beaa3491d1808c00f57095af22faa2b0a0f3e39410e4667d0d2a3bf4deb3b988fa06599b5e7367ed18c498a10ad15eae535c37439ea81d42e1a178b300be71a89faeb555e61ec5a739d7cc09ee31a8d1d9410d85c765affb49691b89dd7e8c96f2052891c37a3dbd685dbd8103fd6bae48cc4bc4b40ac67c9a407e79da38f6d69510883dea54c983b2d1f8b96d2e1d47b315e8f968880b50edd8a559a63f6a9e64fa3c35422805142d9a59b83dd81c8c62efe33a69d59ea699943702db691666969b25dde348bc526cdb2dd17f385d1cd24ceadac7c6406d829101112131415161718191a1b1c1d1e1ff18552e604e0f61119780f85e5170bae73210427333e80f7526781a5930f88f62af1f45e07fd959775708017a09c63006750fa0e07706cb85c7ae9fb3009a7b3";
    private const string V3_page_2 =
        "32cec84537a998b11b3c02825b11b3a051eeef51ba1fe2201b826be2be877a55ba9e6a32bff7321e786bd15d753b79277892115f759e4a089e1c3b67b3e942868489d6ade07b2bef49a16df892cc0831657bf3c130878a9e964347687df867146e8fc2d685dbd159b3b3e5009aea545919c6e8626bb141c225e3157b9c9c244854c07832dd73c1cf3056ec10f935bd5e505acf918e4f71e6891ad8f97a9c5571accb49249310740115e68a27ea7da3685c6c8a763bd77c947f1dde013f961fd7b800a68aabae13687abaef06bac8932e894572545f06c3f9791386f2f01a8c86238172f5b87c582de3d8e7f73ab146b5c978b4fe3c7f17615146eee5eb1070cff35f5788880c21580e83131c01817c274ecdcee33fcd7cb5ce47aefbae6815f5f4e1896cae26d8bf19434dc964cfe4b6e5712404e99d8f6883026861c1f859bc7308248e91387884281e08740f8044c821aea3c39d81fe4c311a5e6ec36824161bbfd0d04402b02cf37520d4690eda464810cca98a1b102c514428640a24f4789f28f69e1725afeaed02ea17ba4cfb7c44fa0a24a8e17d861fd836d41a75a39f51e0dcf0e0217b3dcb947a4a99d5e9c8e15b548c5806fe5060534a1bb1b3289177b3a3c9ba80857fac9fd33a6e8d5c9947d11efeb3aa57ce5cb86ccae4e17641976262c4cbf076219ebd509618d757f7cf25eb2b3360685c86db8ba134f6f2a3788c407f457d2d01799c30323064c8f16b2bae1d8bf90063114561994bf099b6bcb03241c9807d4d0403df1b59220807a0c15cd9d74fffe595d30504f3fd6f559f16ece6f8b28cfecaf217c245b540199b26153bad6534f1d5cc6cc47c1da21929f3a10b026b0e81c3673a696c77bbf9dfef6883850581f09b7f62d82724c647ebf47d8a10595894d1b3af8a3e6a49873080a4a3346f912b4948787bb5b69125871709ec24ef3ed978781ee89f26f85afb70eddc8e32cc7b1c8be8996f18ae291e7fe0898c07b41375ebf6b0e900981f31cb52fe493d97d9e797ae369b0d6dd7befce63cf048015e7049fdb8945dd2f45b515b0503234b9cc66ccb1cb01636943130c3563c0e84c4c6e83afee6f51aec64376527628f4a22295f0910887192524001f97dc7ee069a9bc221e24bd1592c8fb0e12072543d0d487bc938ed0ec5f22ed3a6ca7d7c9a75b4642245c66937bd01750bef73b8ddf47e6f34a6c93b19983d6eaa8df70f812a14b66b40b5da296f50784c9895d23531ae26dc68dfff2a72708f735d9bc6c8105649e34d1c7c076b644603afa8a9e80df5040a7b6a775bcf1924bd5ee3a00a8153f84b7f7fe61d8d9cd717307106b152a0ee98c91dd87232e378614e863fb4961a167b1e1d8bbe99101112131415161718191a1b1c1d1e1f3e530f1224524b888337c89a1d8420162fc0dda6000000000000000000000000";

    private static readonly byte[] Salt = Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf");
    private static readonly byte[] Iv = Convert.FromHexString("101112131415161718191a1b1c1d1e1f");

    private static byte[] Expected(int number, int size, int from) // the generator's plaintext: byte i = (i * 7 + page number) & 0xFF
    {
        var p = new byte[size];
        for (int i = from; i < size; i++)
        {
            p[i] = (byte)((i * 7 + number) & 0xFF);
        }

        return p;
    }

    [Theory]
    [InlineData("V4", 2)]
    [InlineData("V4", 1)]
    [InlineData("V3", 2)]
    public void IndependentlyBuiltPagesDecode(string set, int number)
    {
        CipherSettings s = set == "V4" ? CipherSettings.V4 : CipherSettings.V3;
        s = s.WithPageSize(1024);
        string hex = set == "V3" ? V3_page_2 : number == 2 ? V4_page_2 : V4_page_1;
        byte[] stored = Convert.FromHexString(hex);
        using CipherKey key = CipherKey.FromPassphrase("test123");
        using PageCodec codec = PageCodec.OpenExisting(s, key, stored, Salt);
        Assert.Equal(Salt, codec.Salt.ToArray());
        byte[] plain = new byte[1024];
        Assert.True(codec.TryDecode((uint)number, stored, plain));

        byte[] want = Expected(number, 1024, number == 1 ? 16 : 0);
        int usable = 1024 - codec.ReserveSize;
        Assert.Equal(want[(number == 1 ? 16 : 0)..usable], plain[(number == 1 ? 16 : 0)..usable]);
        if (number == 1)
        {
            Assert.Equal("SQLite format 3\0"u8.ToArray(), plain[..16]);
        }

        // The IV in the page is the one the generator chose, and our own encoding of the same plaintext is a different, equally valid page.
        Assert.Equal(Iv, stored[usable..(usable + 16)]);
        byte[] again = new byte[1024];
        codec.Encode((uint)number, plain, again);
        Assert.False(again.AsSpan(usable, 16).SequenceEqual(Iv));
        byte[] back = new byte[1024];
        Assert.True(codec.TryDecode((uint)number, again, back));
        Assert.Equal(plain, back);

        // Wrong passphrase, wrong page number, one flipped bit.
        using CipherKey wrong = CipherKey.FromPassphrase("test124");
        using PageCodec bad = PageCodec.OpenExisting(s, wrong, stored, Salt);
        Assert.False(bad.TryDecode((uint)number, stored, plain));
        Assert.False(codec.TryDecode((uint)number + 5, stored, plain));
        stored[100] ^= 0x80;
        Assert.False(codec.TryDecode((uint)number, stored, plain));
    }
}
