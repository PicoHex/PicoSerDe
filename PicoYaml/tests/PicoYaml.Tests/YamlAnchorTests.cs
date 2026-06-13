namespace PicoYaml.Tests;

public class YamlAnchorTests
{
    [Test]
    public async Task SimpleAnchor_DefineAndReference_ResolvesValue()
    {
        var yaml = "name: &label Alice\ncopy: *label"u8.ToArray();
        string k1,
            v1,
            k2,
            v2;
        using (var reader = new YamlReader(yaml))
        {
            reader.Read();
            k1 = Encoding.UTF8.GetString(reader.KeySpan);
            v1 = Encoding.UTF8.GetString(reader.ValueSpan);
            reader.Read();
            k2 = Encoding.UTF8.GetString(reader.KeySpan);
            v2 = Encoding.UTF8.GetString(reader.ValueSpan);
        }
        await Assert.That(k1).IsEqualTo("name");
        await Assert.That(v1).IsEqualTo("Alice");
        await Assert.That(k2).IsEqualTo("copy");
        await Assert.That(v2).IsEqualTo("Alice"); // alias resolves to Alice
    }

    [Test]
    public async Task UnresolvedAlias_ThrowsFormatException()
    {
        var yaml = "key: *undefined"u8.ToArray();
        using (var reader = new YamlReader(yaml))
        {
            try
            {
                reader.Read();
                throw new Exception("Expected FormatException");
            }
            catch (FormatException) { }
        }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task DuplicateAnchor_ThrowsFormatException()
    {
        var yaml = "a: &dup 1\nb: &dup 2"u8.ToArray();
        using (var reader = new YamlReader(yaml))
        {
            try
            {
                while (reader.Read()) { }
                throw new Exception("Expected FormatException");
            }
            catch (FormatException) { }
        }
        await Assert.That(true).IsTrue();
    }

    // ── Merge key (<<:) ──

    [Test]
    public async Task MappingAnchor_StoresPairs()
    {
        // Verify &def on a mapping stores key-value pairs
        var yaml = "defaults: &def\n  host: localhost\n  port: 8080"u8.ToArray();
        var keys = new List<string>();
        using (var reader = new YamlReader(yaml))
        {
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.PropertyName)
                    keys.Add(Encoding.UTF8.GetString(reader.KeySpan));
            }
        }
        await Assert.That(keys).Contains("defaults");
        await Assert.That(keys).Contains("host");
        await Assert.That(keys).Contains("port");
    }

    [Test]
    public async Task MergeKey_MergesAnchorMapping()
    {
        // server section merges def into itself:
        //   server:
        //     <<: *def      (brings host=localhost, port=8080)
        //     name: main     (explicit override)
        var yaml =
            "defaults: &def\n  host: localhost\n  port: 8080\nserver:\n  <<: *def\n  name: main"u8.ToArray();
        bool inServer = false;
        var serverKeys = new List<string>();
        var serverVals = new List<string>();
        using (var reader = new YamlReader(yaml))
        {
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.ObjectStart)
                    continue;
                if (reader.TokenType == TokenType.ObjectEnd)
                {
                    inServer = false;
                    continue;
                }
                if (reader.TokenType == TokenType.PropertyName)
                {
                    var key = Encoding.UTF8.GetString(reader.KeySpan);
                    if (key == "server")
                    {
                        inServer = true;
                        continue;
                    }
                    if (inServer)
                    {
                        serverKeys.Add(key);
                        serverVals.Add(Encoding.UTF8.GetString(reader.ValueSpan));
                    }
                }
            }
        }
        // After P1 fix: cross-mapping anchor alias should correctly resolve
        // the anchored mapping's key-value pairs into the server section.
        await Assert.That(serverKeys).Contains("host");
        await Assert.That(serverKeys).Contains("port");
        await Assert.That(serverKeys).Contains("name");
        // Verify values round-trip correctly
        var hostIdx = serverKeys.IndexOf("host");
        if (hostIdx >= 0)
            await Assert.That(serverVals[hostIdx]).IsEqualTo("localhost");
    }

    // P3: Self-referencing merge key — *alias used within same mapping that defines &anchor
    [Test]
    public async Task MergeKey_SelfReferencing_AnchorInSameMapping()
    {
        // server section defines &def on itself and uses <<: *def within:
        //   server: &def
        //     host: localhost
        //     port: 8080
        //     <<: *def       ← P1 fix: _nextAnchorName is set before ObjectStart
        //     name: main
        var yaml =
            "server: &def\n  host: localhost\n  port: 8080\n  <<: *def\n  name: main"u8.ToArray();
        var keys = new List<string>();
        var vals = new List<string>();
        bool inServer = false;
        using (var reader = new YamlReader(yaml))
        {
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.ObjectStart)
                {
                    inServer = true;
                    continue;
                }
                if (reader.TokenType == TokenType.ObjectEnd)
                {
                    inServer = false;
                    continue;
                }
                if (reader.TokenType == TokenType.PropertyName && inServer)
                {
                    keys.Add(Encoding.UTF8.GetString(reader.KeySpan));
                    vals.Add(Encoding.UTF8.GetString(reader.ValueSpan));
                }
            }
        }
        // <<: merge key is emitted as a regular PropertyName token — it's up to
        // the SG-generated deserializer to interpret the merge directive.
        // host and port should appear (from the merge via alias resolution)
        // name should appear as override
        await Assert.That(keys).Contains("host");
        await Assert.That(keys).Contains("port");
        await Assert.That(keys).Contains("name");
    }

    // P3: Verify self-referencing alias no longer throws after fix
    [Test]
    public async Task MergeKey_SelfReferencing_NoExceptionAfterFix()
    {
        // After P3 fix, self-referencing alias should resolve successfully
        // by using the in-progress _pendingMappingAnchor / _currentMappingPairs
        var yaml =
            "server: &def\n  host: localhost\n  port: 8080\n  <<: *def\n  name: main"u8.ToArray();
        Exception? ex = null;
        using (var reader = new YamlReader(yaml))
        {
            try
            {
                while (reader.Read()) { }
            }
            catch (FormatException e)
            {
                ex = e;
            }
        }
        // After fix: no exception should be thrown
        await Assert.That(ex).IsNull();
    }

    // Multi-anchor inline storage test
    [Test]
    public async Task MultipleAnchors_WithinInlineLimit_AllResolve()
    {
        var yaml = "a: &a1 Alice\nb: &a2 Bob\nc: &a3 Charlie\nx: *a1\ny: *a2\nz: *a3"u8.ToArray();
        var results = new Dictionary<string, string>();
        using (var reader = new YamlReader(yaml))
        {
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.PropertyName)
                {
                    var k = Encoding.UTF8.GetString(reader.KeySpan);
                    var v = Encoding.UTF8.GetString(reader.ValueSpan);
                    if (k is "x" or "y" or "z")
                        results[k] = v;
                }
            }
        }
        await Assert.That(results["x"]).IsEqualTo("Alice");
        await Assert.That(results["y"]).IsEqualTo("Bob");
        await Assert.That(results["z"]).IsEqualTo("Charlie");
    }

    [Test]
    public async Task MappingAnchor_OverMaxPairs_ThrowsFormatException()
    {
        // P0 fix: anchored mappings with >8 key-value pairs should throw.
        var yaml =
            "defaults: &def\n  f01: v01\n  f02: v02\n  f03: v03\n  f04: v04\n  f05: v05\n  f06: v06\n  f07: v07\n  f08: v08\n  f09: v09"u8.ToArray();
        var threw = false;
        {
            var reader = new YamlReader(yaml);
            try
            {
                while (reader.Read()) { }
            }
            catch (FormatException)
            {
                threw = true;
            }
            reader.Dispose();
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task MappingAnchor_AtMaxPairs_Succeeds()
    {
        // P0 fix: exactly 8 pairs should succeed.
        var yaml =
            "defaults: &def\n  f01: v01\n  f02: v02\n  f03: v03\n  f04: v04\n  f05: v05\n  f06: v06\n  f07: v07\n  f08: v08"u8.ToArray();
        var threw = false;
        {
            var reader = new YamlReader(yaml);
            try
            {
                while (reader.Read()) { }
            }
            catch (FormatException)
            {
                threw = true;
            }
            reader.Dispose();
        }
        await Assert.That(threw).IsFalse();
    }

    [Test]
    public async Task MultipleMappingAnchors_AliasToFirst_ResolvesCorrectly()
    {
        // P7: When two anchored mappings exist, alias to the first must
        // correctly resolve its own data, not the second mapping's data.
        // The accumulator shares anchor 0's storage fields; when the
        // second anchored mapping accumulates, it must not corrupt anchor 0.
        // Use distinct keys (fa/fb) so corruption is visible as wrong key
        // in the replayed token sequence.
        var yaml = "first: &a\n  fa: va\nsecond: &b\n  fb: vb\nresult: *a\nck: final"u8.ToArray();
        var tokens = new List<(string Key, string Val)>();
        {
            var reader = new YamlReader(yaml);
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.PropertyName)
                {
                    tokens.Add(
                        (
                            Encoding.UTF8.GetString(reader.KeySpan),
                            Encoding.UTF8.GetString(reader.ValueSpan)
                        )
                    );
                }
            }
            reader.Dispose();
        }
        // Token order should be:
        // first, fa, second, fb, result, fa, ck
        //                    ^ replay begins ^  ^ ck is after replay
        // Bug: replay emits "fb" instead of "fa" because anchor 0 corrupted
        var resultIdx = tokens.FindIndex(t => t.Key == "result");
        await Assert.That(resultIdx).IsGreaterThanOrEqualTo(0);
        // The token IMMEDIATELY after "result" should be the replayed "fa"
        await Assert.That(tokens[resultIdx + 1].Key).IsEqualTo("fa");
        await Assert.That(tokens[resultIdx + 1].Val).IsEqualTo("va");
    }
}
