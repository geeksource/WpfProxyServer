using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfProxyServer
{
    using System;
    using System.Data.SQLite;
    using System.Text;

    public class DatabaseLogger
    {
        private readonly string _connectionString;

        public DatabaseLogger(string connectionString)
        {
            _connectionString = connectionString;
            EnsureTableExists();
        }

        private void EnsureTableExists()
        {
            using (var connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                string query = @"
                CREATE TABLE IF NOT EXISTS PostRequests (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Url TEXT NOT NULL,
                    Headers TEXT,
                    Body TEXT,
                    Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
                );";

                using (var cmd = new SQLiteCommand(query, connection))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void LogPostRequest(string url, string headers, string body)
        {
            using (var connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                string query = "INSERT INTO PostRequests (Url, Headers, Body) VALUES (@Url, @Headers, @Body)";

                using (var cmd = new SQLiteCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@Url", url);
                    cmd.Parameters.AddWithValue("@Headers", headers);
                    cmd.Parameters.AddWithValue("@Body", body);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }

}
