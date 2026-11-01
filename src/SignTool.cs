// Подпись релиза. В программу не входит, нужна только при сборке.
// genkey <файл>: создать ключ, в консоль - открытая часть для Updater.PublicKey
// sign <ключ> <файл>: создать <файл>.sig
using System;
using System.IO;
using System.Security.Cryptography;

static class SignTool
{
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "genkey")
        {
            if (File.Exists(args[1])) { Console.Error.WriteLine("key already exists: " + args[1]); return 1; }
            using (var rsa = new RSACryptoServiceProvider(3072))
            {
                File.WriteAllText(args[1], rsa.ToXmlString(true));
                Console.WriteLine(rsa.ToXmlString(false));
            }
            return 0;
        }
        if (args.Length == 3 && args[0] == "sign")
        {
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.FromXmlString(File.ReadAllText(args[1]));
                File.WriteAllBytes(args[2] + ".sig", rsa.SignData(File.ReadAllBytes(args[2]), "SHA256"));
            }
            return 0;
        }
        Console.Error.WriteLine("usage: genkey <key> | sign <key> <file>");
        return 2;
    }
}
