Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Threading

Module Program

    Sub Main()
        Try
            Dim _mining As New MiningClient("localhost", "8081", "Avd30BPaulA+QaPsvXLBQrO2o/JmbOQ7USBCxca7hGcU")
            _mining.Start()

            ' Wait for user input to stop the server
            Console.WriteLine("Press Enter to stop the server...")
            Console.ReadLine()

            _mining.Kill()

        Catch ex As Exception
            Console.WriteLine($"Error: {ex.Message}")
        End Try
    End Sub

End Module