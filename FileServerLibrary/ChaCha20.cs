namespace FileServerLibrary;

public class ChaCha20: IDisposable
{
    private const int KeyStreamLengthBytes = 64;
    
    private static readonly uint[] Magic =
    {
        1634760805U,
        857760878U,
        2036477234U,
        1797285236U
    };
    
    private readonly uint[] _keystream;
    private int _position;

    private readonly uint[] _state;
    
    public ChaCha20(byte[] key, byte[] iv)
    {
        _state = new uint[16];
        _state[0] = Magic[0];
        _state[1] = Magic[1];
        _state[2] = Magic[2];
        _state[3] = Magic[3];
        _state[4] = BitConverter.ToUInt32(key, 0);
        _state[5] = BitConverter.ToUInt32(key, 4);
        _state[6] = BitConverter.ToUInt32(key, 8);
        _state[7] = BitConverter.ToUInt32(key, 12);
        _state[8] = BitConverter.ToUInt32(key, 16);
        _state[9] = BitConverter.ToUInt32(key, 20);
        _state[10] = BitConverter.ToUInt32(key, 24);
        _state[11] = BitConverter.ToUInt32(key, 28);
        _state[12] = 0;
        _state[13] = BitConverter.ToUInt32(iv, 0);
        _state[14] = BitConverter.ToUInt32(iv, 4);
        _state[15] = BitConverter.ToUInt32(iv, 8);
        
        _keystream = new uint[16];
        _position = KeyStreamLengthBytes;
    }

    private static uint RotL32(uint x, int n)
    {
        return (x << n) | (x >> (32 - n));
    }

    private byte GetKeyStreamByte()
    {
        var v = _keystream[_position / 4];
        switch (_position++ % 4)
        {
            case 0: return (byte)(v & 0xFF);
            case 1: return (byte)((v & 0xFF00) >> 8);
            case 2: return (byte)((v & 0xFF0000) >> 16);
            default: return (byte)(v >> 24);
        }
    }
    
    private void QuarterRound(int a, int b, int c, int d)
    {
        _keystream[a] += _keystream[b];
        _keystream[d] = RotL32(_keystream[d] ^ _keystream[a], 16);
        _keystream[c] += _keystream[d];
        _keystream[b] = RotL32(_keystream[b] ^ _keystream[c], 12);
        _keystream[a] += _keystream[b];
        _keystream[d] = RotL32(_keystream[d] ^ _keystream[a], 8);
        _keystream[c] += _keystream[d];
        _keystream[b] = RotL32(_keystream[b] ^ _keystream[c], 7);
    }

    private void NextBlock()
    {
        for (var i = 0; i < 16; i++)
            _keystream[i] = _state[i];
        for (var i = 0; i < 10; i++)
        {
            QuarterRound(0, 4, 8, 12);
            QuarterRound(1, 5, 9, 13);
            QuarterRound(2, 6, 10, 14);
            QuarterRound(3, 7, 11, 15);
            QuarterRound(0, 5, 10, 15);
            QuarterRound(1, 6, 11, 12);
            QuarterRound(2, 7, 8, 13);
            QuarterRound(3, 4, 9, 14);
        }
        for (var i = 0; i < 16; i++)
            _keystream[i] += _state[i];
        _state[12]++;
        if (_state[12] == 0)
            _state[13]++;
    }
    
    public byte[] Encrypt(byte[] data)
    {
        var result = new byte[data.Length];
        var offset = 0;
        while (offset < data.Length)
        {
            if (_position == KeyStreamLengthBytes)
            {
                NextBlock();
                _position = 0;
            }

            var l = Math.Min(KeyStreamLengthBytes - _position, data.Length - offset);
            while (l > 0)
            {
                result[offset] = (byte)(data[offset] ^ GetKeyStreamByte());
                offset++;
                l--;
            }
        }
        return result;
    }

    public void Dispose()
    {
        Array.Clear(_keystream);
        Array.Clear(_state);
    }
}