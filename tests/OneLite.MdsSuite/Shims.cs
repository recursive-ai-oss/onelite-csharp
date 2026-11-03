// Glue between Microsoft.Data.Sqlite's test sources and the released package. Nothing here is copied from the library.

using System;
using System.Reflection;
using RecursiveAI.OneLite.SQLitePCLRaw;

namespace SQLitePCL
{
    /// <summary>The suite starts its provider with <c>Batteries_V2.Init()</c>; here that selects OneLite, which is the whole point of the run.</summary>
    public static class Batteries_V2
    {
        public static void Init() => OneLiteBatteries.Init();
    }
}

namespace Microsoft.Data.Sqlite.Properties
{
    /// <summary>
    /// The suite compares exception messages with the library's internal resource strings. The library only lets its own test assemblies
    /// (signed with Microsoft's key) see them, so this class asks the released library for the same strings at run time.
    /// </summary>
    internal static class Resources
    {
        private static readonly Type s_type = typeof(SqliteConnection).Assembly.GetType("Microsoft.Data.Sqlite.Properties.Resources")!;
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static string Prop(string name) => (string)s_type.GetProperty(name, Flags)!.GetValue(null)!;

        private static string Call(string name, object?[] args) => (string)s_type.GetMethod(name, Flags)!.Invoke(null, args)!;

        public static string NoData => Prop(nameof(NoData));
        public static string TransactionCompleted => Prop(nameof(TransactionCompleted));
        public static string InvalidOffsetAndCount => Prop(nameof(InvalidOffsetAndCount));
        public static string ResizeNotSupported => Prop(nameof(ResizeNotSupported));
        public static string WriteNotSupported => Prop(nameof(WriteNotSupported));
        public static string TransactionRequired => Prop(nameof(TransactionRequired));
        public static string TransactionConnectionMismatch => Prop(nameof(TransactionConnectionMismatch));
        public static string SqlBlobRequiresOpenConnection => Prop(nameof(SqlBlobRequiresOpenConnection));
        public static string SeekBeforeBegin => Prop(nameof(SeekBeforeBegin));
        public static string ParallelTransactionsNotSupported => Prop(nameof(ParallelTransactionsNotSupported));
        public static string DataReaderOpen => Prop(nameof(DataReaderOpen));
        public static string ConnectionStringRequiresClosedConnection => Prop(nameof(ConnectionStringRequiresClosedConnection));
        public static string CannotStoreNaN => Prop(nameof(CannotStoreNaN));

        public static string AmbiguousColumnName(params object?[] a) => Call(nameof(AmbiguousColumnName), a);
        public static string AmbiguousParameterName(params object?[] a) => Call(nameof(AmbiguousParameterName), a);
        public static string CalledOnNullValue(params object?[] a) => Call(nameof(CalledOnNullValue), a);
        public static string CallRequiresOpenConnection(params object?[] a) => Call(nameof(CallRequiresOpenConnection), a);
        public static string DataReaderClosed(params object?[] a) => Call(nameof(DataReaderClosed), a);
        public static string EncryptionNotSupported(params object?[] a) => Call(nameof(EncryptionNotSupported), a);
        public static string InvalidCommandType(params object?[] a) => Call(nameof(InvalidCommandType), a);
        public static string InvalidEnumValue(params object?[] a) => Call(nameof(InvalidEnumValue), a);
        public static string InvalidIsolationLevel(params object?[] a) => Call(nameof(InvalidIsolationLevel), a);
        public static string InvalidParameterDirection(params object?[] a) => Call(nameof(InvalidParameterDirection), a);
        public static string KeywordNotSupported(params object?[] a) => Call(nameof(KeywordNotSupported), a);
        public static string MissingParameters(params object?[] a) => Call(nameof(MissingParameters), a);
        public static string RequiresSet(params object?[] a) => Call(nameof(RequiresSet), a);
        public static string SetRequiresNoOpenReader(params object?[] a) => Call(nameof(SetRequiresNoOpenReader), a);
        public static string SqliteNativeError(params object?[] a) => Call(nameof(SqliteNativeError), a);
        public static string TooManyRestrictions(params object?[] a) => Call(nameof(TooManyRestrictions), a);
        public static string UDFCalledWithNull(params object?[] a) => Call(nameof(UDFCalledWithNull), a);
        public static string UnknownCollection(params object?[] a) => Call(nameof(UnknownCollection), a);
        public static string UnknownDataType(params object?[] a) => Call(nameof(UnknownDataType), a);
    }
}

namespace Microsoft.Data.Sqlite
{
    /// <summary>Helpers the suite calls on a connection; the library keeps its own versions internal. Written from how the tests use them.</summary>
    internal static class SqliteConnectionHelpers
    {
        public static int ExecuteNonQuery(this SqliteConnection connection, string commandText, params SqliteParameter[] parameters)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Parameters.AddRange(parameters);
            return command.ExecuteNonQuery();
        }

        public static T ExecuteScalar<T>(this SqliteConnection connection, string commandText, params SqliteParameter[] parameters)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Parameters.AddRange(parameters);
            object? value = command.ExecuteScalar();
            if (value is T direct)
            {
                return direct;
            }

            if (value is null || value is DBNull)
            {
                return default!;
            }

            return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>The connection's current transaction, which the library exposes only to its own tests.</summary>
        public static object? TransactionOf(SqliteConnection connection)
            => typeof(SqliteConnection).GetProperty("Transaction", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.GetValue(connection);
    }
}
