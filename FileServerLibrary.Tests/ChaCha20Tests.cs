using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;

namespace FileServerLibrary.Tests;

[TestFixture]
[TestOf(typeof(ChaCha20CryptoPlugin))]
public class ChaCha20Tests
{
    [Test]
    public void ChaCha20Test()
    {
        var parameters =
            new ServerConfigurationParameters(
                new Dictionary<string, Type>(),
                new Dictionary<string, JsonElement>());
        var crypto = new ChaCha20CryptoPlugin(parameters);
        var key = RandomNumberGenerator.GetBytes(32);
        var data = RandomNumberGenerator.GetBytes(1024);
        var encrypted = crypto.Encrypt(key, data);
        Assert.That(encrypted, Has.Length.EqualTo(data.Length + 12 + 32 + 12 + 32));
        var withPrefix = new byte[encrypted.Length + 4];
        Array.Copy(encrypted, 0, withPrefix, 4, encrypted.Length);
        var decrypted = crypto.Decrypt(key, withPrefix, 4);
        Assert.That(decrypted, Is.EqualTo(data));
    }
}