namespace uqoz.Security

open System
open System.IO
open System.Security.Cryptography
open System.Text

module Encryption =
    let private SecretKey = "uqoz_super_secret_key_32bytes!!"

    // SHA-256 всегда даёт корректные 32 байта для AES-256,
    // независимо от длины парольной фразы.
    let private keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes SecretKey)

    let encrypt (plainText: string) : string =
        try
            use aes = Aes.Create()
            aes.Key <- keyBytes
            aes.GenerateIV()
            use encryptor = aes.CreateEncryptor(aes.Key, aes.IV)
            use ms = new MemoryStream()
            ms.Write(aes.IV, 0, aes.IV.Length)
            use cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write)
            use sw = new StreamWriter(cs)
            sw.Write(plainText)
            sw.Flush()
            cs.FlushFinalBlock()
            Convert.ToBase64String(ms.ToArray())
        with _ -> plainText

    let decrypt (cipherText: string) : string =
        try
            let fullBytes = Convert.FromBase64String(cipherText)
            use aes = Aes.Create()
            let iv = Array.zeroCreate<byte> 16
            Array.Copy(fullBytes, 0, iv, 0, 16)
            aes.Key <- keyBytes
            aes.IV <- iv
            use decryptor = aes.CreateDecryptor(aes.Key, aes.IV)
            use ms = new MemoryStream(fullBytes, 16, fullBytes.Length - 16)
            use cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read)
            use sr = new StreamReader(cs)
            sr.ReadToEnd()
        with _ -> cipherText

/// AES-256 with the IV stored separately, used by ChatService.
namespace uqoz.Crypto

open System
open System.IO
open System.Text
open System.Security.Cryptography

module CryptoService =

    let Encrypt (plainText: string) (key: byte[]) : string * byte[] =
        use aes = Aes.Create()
        aes.Key <- key
        aes.GenerateIV()
        use encryptor = aes.CreateEncryptor(aes.Key, aes.IV)
        use ms = new MemoryStream()
        use cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write)
        let bytes = Encoding.UTF8.GetBytes plainText
        cs.Write(bytes, 0, bytes.Length)
        cs.FlushFinalBlock()
        Convert.ToBase64String(ms.ToArray()), aes.IV

    let Decrypt (cipherText: string) (key: byte[]) (iv: byte[]) : string =
        use aes = Aes.Create()
        aes.Key <- key
        aes.IV <- iv
        use decryptor = aes.CreateDecryptor(aes.Key, aes.IV)
        let fullBytes = Convert.FromBase64String cipherText
        use ms = new MemoryStream(fullBytes)
        use cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read)
        use sr = new StreamReader(cs)
        sr.ReadToEnd()