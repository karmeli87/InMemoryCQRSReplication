using System;
using Akka.Configuration;

namespace Akka.CQRS.Infrastructure
{
    /// <summary>
    /// Shared utility class for formatting RavenDB entry url into the required
    /// Akka.Persistence.Sql HOCON <see cref="Akka.Configuration.Config"/>.
    /// </summary>
    public static class RavenDbHoconHelper
    {
        public static Configuration.Config GetRavenDbHocon(string url, string database)
        {
            var hocon = $@"
akka.persistence.journal.ravendb {{
    urls = [""{url}""]
    name = ""{database}""
}}
akka.persistence.query.journal.ravendb {{
}}
akka.persistence.snapshot-store.ravendb {{
}}";
            return hocon;
        }
    }
}
