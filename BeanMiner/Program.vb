Imports System.Threading

Module Program
    Private miningClient As MiningClient

    Sub Main()
        Console.Title = "VB.NET Mining Client"
        Dim minerPubKey As String = "AilsDcSptGDP7EfXGV7aSqWP3aJQtF0oLMe+ziMztg0A" ' IMPORTANT: Replace this!

        If minerPubKey = "YOUR_PUBLIC_KEY_HERE_BASE64_ENCODED" Then
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")
            Console.WriteLine("!! IMPORTANT: Please open Program.vb and set your minerPublicKey !!")
            Console.WriteLine("!! This should be the Base64 encoded public key from your wallet. !!")
            Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")
            Console.ResetColor()
            Console.WriteLine("Press Enter to exit.")
            Console.ReadLine()
            Return
        End If


        Console.WriteLine("Starting Mining Client...")
        Try
            '                                Server IP, Port, Your Miner Public Key
            'miningClient = New MiningClient("localhost", 8081, minerPubKey)
            miningClient = New MiningClient("192.168.18.9", 8081, minerPubKey)
            miningClient.Start()

            Console.WriteLine("Mining client started. Press Enter to stop.")
            Console.ReadLine()

        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"Critical error during client startup: {ex.Message}")
            Console.ResetColor()
            Console.WriteLine("Press Enter to exit.")
            Console.ReadLine()
        Finally
            If miningClient IsNot Nothing Then
                Console.WriteLine("Stopping mining client...")
                miningClient.Kill()
            End If
            Console.WriteLine("Client shut down. Press Enter to exit application.")
            Console.ReadLine()
        End Try
    End Sub

End Module