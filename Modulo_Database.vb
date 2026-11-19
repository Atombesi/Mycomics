Imports System.IO
Imports Microsoft.Data.Sqlite

Public Module Modulo_Database

    ' Percorso del database SQLite.
    Public Gbl_DataBase As String = "G:\My\My_Sorgenti\Visualstudio\MyComics\MyComics.db"

    Public Function VerificaDatabase() As String

        If Not File.Exists(Gbl_DataBase) Then
            Return "Database non trovato: " & Gbl_DataBase
        End If

        Try
            Dim configurazione As New SqliteConnectionStringBuilder()
            configurazione.DataSource = Gbl_DataBase
            configurazione.Mode = SqliteOpenMode.ReadOnly

            Using connessione As New SqliteConnection(configurazione.ToString())

                connessione.Open()

                Dim nomiTabelle As String() = {
                    "COLLANE",
                    "ALBI",
                    "MYCOPIE",
                    "POSIZIONI"
                }

                For Each nomeTabella As String In nomiTabelle

                    Dim istruzioneSql As String =
                        "SELECT COUNT(*) FROM sqlite_master " &
                        "WHERE type = 'table' AND name = @NomeTabella"

                    Using comando As New SqliteCommand(istruzioneSql, connessione)

                        comando.Parameters.AddWithValue("@NomeTabella", nomeTabella)

                        Dim numeroTabelle As Integer = Convert.ToInt32(comando.ExecuteScalar())

                        If numeroTabelle = 0 Then
                            Return "Tabella mancante: " & nomeTabella
                        End If

                    End Using

                Next

            End Using

            Return "Database collegato correttamente."

        Catch errore As Exception

            Return "Errore SQLite: " & errore.Message

        End Try

    End Function

End Module
