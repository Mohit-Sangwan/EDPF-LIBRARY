using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Edpf.Abstractions.Security;
using Edpf.WalkingSkeleton.Api.Infrastructure.Security;

namespace Edpf.Benchmarks;

/// <summary>
/// Baseline entries for the Z.18 publication set: encrypt/decrypt per field
/// size and envelope serialization cost. Seeds EDPF-BNC-001 (Phase 02 §⑤
/// demonstration 10); Phase 31 adds the pinned-hardware CI gate.
/// </summary>
[MemoryDiagnoser]
public class CryptoEnvelopeBenchmarks
{
    private readonly AesGcmAlgorithm _algorithm = new();
    private byte[] _key = [];
    private byte[] _nonce = [];
    private byte[] _plaintext = [];
    private byte[] _wire = [];

    /// <summary>Field sizes: a short MRN, a paragraph, a small document.</summary>
    [Params(32, 1_024, 65_536)]
    public int PlaintextBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _key = RandomNumberGenerator.GetBytes(32);
        _nonce = RandomNumberGenerator.GetBytes(EncryptionEnvelope.NonceSize);
        _plaintext = RandomNumberGenerator.GetBytes(PlaintextBytes);

        (byte[] ciphertext, byte[] tag) = _algorithm.Encrypt(_plaintext, _key, _nonce);
        _wire = new EncryptionEnvelope(
            EncryptionEnvelope.CurrentVersion, _algorithm.Id, Guid.NewGuid(), 1, _nonce, ciphertext, tag)
            .Serialize();
    }

    [Benchmark(Baseline = true)]
    public (byte[], byte[]) EncryptField() => _algorithm.Encrypt(_plaintext, _key, _nonce);

    [Benchmark]
    public EncryptionEnvelope DeserializeEnvelope() => EncryptionEnvelope.Deserialize(_wire);

    [Benchmark]
    public byte[] SerializeRoundTrip() => EncryptionEnvelope.Deserialize(_wire).Serialize();
}
