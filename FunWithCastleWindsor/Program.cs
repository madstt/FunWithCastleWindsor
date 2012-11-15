using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Castle.Core;
using Castle.MicroKernel.Registration;
using Castle.Windsor;
using NHibernate;
using NHibernate.Cfg;
using NHibernate.Context;
using NHibernate.Linq;
using NHibernate.Tool.hbm2ddl;

namespace FunWithCastleWindsor
{
    class Program
    {
        static void Main(string[] args)
        {
            const string configFilePath = @"hibernate.cfg.xml";

            NHibernateHelper.InitializeSessionFactory(configFilePath, new List<Assembly>() { typeof(Mapping).Assembly });

            WindsorContainer container = new WindsorContainer();

            container.Register(Component.For(typeof(IRepository<>)).ImplementedBy(typeof(NHibernateRepository<>)).LifeStyle.Is(LifestyleType.Transient));

            container.Register(Component.For<ISession>().LifeStyle.Transient.UsingFactoryMethod(kernel => UnitOfWork.GetCurrentSession()));

            var systemInfoRepo = container.Resolve(typeof(IRepository<SystemInfo>));

            var systemElementRepo = container.Resolve(typeof(IRepository<SystemElement>));
        }
    }

    public class SystemInfo : PersistenEntity
    {}

    public class SystemElement : PersistenEntity
    {}

    public class Mapping : PersistenEntity
    {
    }


    public abstract class PersistenEntity
    {
    }

    public interface IRepository<T> : INHibernateQueryable<T>
    {
        ISession Session { get; }

        ITransaction BeginTransaction();
        void Delete(T item);
        T Get(object id);
        T Load(object id);
        void Save(T target);
        void Update(T item);
    }

    public class NHibernateRepository<T> : IRepository<T>
    {
        INHibernateQueryable<T> queryable;
        NHibernate.ISession session;

        public NHibernateRepository(NHibernate.ISession session)
        {
            this.session = session;
        }


        INHibernateQueryable<T> AsQueryable
        {
            get
            {
                if (queryable == null)
                    queryable = Session.Linq<T>();
                return queryable;
            }
        }

        public Type ElementType
        {
            get { return AsQueryable.ElementType; }
        }

        public Expression Expression
        {
            get { return AsQueryable.Expression; }
        }

        public IQueryProvider Provider
        {
            get { return AsQueryable.Provider; }
        }

        public QueryOptions QueryOptions
        {
            get { return AsQueryable.QueryOptions; }
        }

        public ISession Session
        {
            get
            {
                return session;
            }
        }


        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public IEnumerator<T> GetEnumerator()
        {
            return AsQueryable.GetEnumerator();
        }

        public IQueryable<T> Expand(string path)
        {
            return AsQueryable.Expand(path);
        }


        #region IRepository members

        public ITransaction BeginTransaction()
        {
            return Session.BeginTransaction();
        }


        public void Delete(T item)
        {
            Session.Delete(item);
        }

        public T Get(object id)
        {
            return Session.Get<T>(id);
        }


        public T Load(object id)
        {
            return Session.Load<T>(id);
        }


        public void Save(T target)
        {
            Session.SaveOrUpdate(target);
        }

        public void Update(T item)
        {
            Session.Update(item);
        }

        #endregion IRepository members

    }

    ///<summary>
    /// The Unit Of Work acts as a static gateway to the thread-static instance
    /// of our ISession.
    ///</summary>
    public class UnitOfWork : IDisposable
    {
        [ThreadStatic]
        static ISession currentSession;

        [ThreadStatic]
        private static bool IsSessionInitialized = false;


        public UnitOfWork()
        {
            var session = GetCurrentSession();
            session.FlushMode = FlushMode.Commit;
            session.BeginTransaction();
        }


        ///<summary>
        /// Gets the currently ongoing session. Creates a new
        /// session if necessary.
        ///</summary>
        ///<returns>Current session</returns>
        public static ISession GetCurrentSession()
        {
            if (!IsSessionInitialized)
            {
                currentSession = NHibernateHelper.SessionFactory.OpenSession();
                CurrentSessionContext.Bind(currentSession);
                IsSessionInitialized = true;
            }

            return NHibernateHelper.SessionFactory.GetCurrentSession();
        }




        public void SetBatchSize(int batchSize)
        {
            var s = NHibernateHelper.SessionFactory;
            GetCurrentSession().SetBatchSize(batchSize);
        }


        public void Flush()
        {
            try
            {
                currentSession.Flush();
            }
            catch (StaleObjectStateException)
            {
                throw new EagleIntegrationException(string.Format("The operation was not completed, since the data was changed or deleted by another transaction."));
            }
        }


        ///<summary>
        /// Commits and ends the current uow
        ///</summary>
        public void Commit()
        {
            try
            {
                currentSession.Transaction.Commit();
            }
            catch (StaleObjectStateException)
            {
                throw new EagleIntegrationException(string.Format("The operation was not completed, since the data was changed or deleted by another transaction."));
            }

        }


        /// <summary>
        /// If commit has not been called explicitly, we will rollback the transaction
        /// </summary>
        public void Dispose()
        {
            //We want to roll back any active transaction. If the user of this class wanted to commit 
            //any updates then he should have to have called UoW.Commit().
            if (currentSession.Transaction != null && currentSession.Transaction.IsActive)
            {
                currentSession.Transaction.Rollback();
            }

            //close the session
            currentSession.Close();

            //Session is disposable, and therefor it's good to call Dispose() once we're done with it.
            currentSession.Dispose();

            //unbind the session from the context as we won't be needing it. 
            UnbindSessionFromSessionFactory();
        }


        private void UnbindSessionFromSessionFactory()
        {
            if (CurrentSessionContext.HasBind(NHibernateHelper.SessionFactory))
            {
                CurrentSessionContext.Unbind(NHibernateHelper.SessionFactory);
            }
            IsSessionInitialized = false;
        }


    }

    public class EagleIntegrationException : Exception
    {
        public EagleIntegrationException(string message)
            : base(message)
        {
        }

        public EagleIntegrationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public EagleIntegrationException()
        {
        }

    }

    public class InfrastructureException : Exception
    {
        public InfrastructureException(string msg)
            : base(msg)
        {
        }
    }

    public static class NHibernateHelper
    {
        private static object KeyHole = new object();

        private static Configuration config = null;
        public static Configuration Config
        {
            get
            {
                lock (KeyHole)
                {
                    if (config == null)
                    {
                        throw new Exception("The configuration has not been created correctly. It should before it is being accessed");
                    }
                    return config;
                }
            }

            private set
            {
                lock (KeyHole)
                {
                    config = value;
                }
            }
        }

        private static ISessionFactory sessionFactory = null;
        public static ISessionFactory SessionFactory
        {
            get
            {
                if (sessionFactory == null)
                {
                    throw new InfrastructureException("The session factory must be initialized before being accessed. Call InitializeSessionFactory to do the initialization.");
                }
                return sessionFactory;
            }
        }


        /// <summary>
        /// Initializes the session factory.
        /// </summary>
        public static void InitializeSessionFactory(string configFilePath, List<Assembly> mappingAssemblies)
        {
            if (sessionFactory != null)
            {
                throw new InfrastructureException("The session factory has already been initialized. Only one call to InitializeSessionFactory is allowed.");
            }

            // Create the configuration
            CreateConfiguration(configFilePath, mappingAssemblies);

            // Create session factory
            sessionFactory = Config.BuildSessionFactory();
        }


        #region Create Configuration and mappings

        public static void CreateConfiguration(string configFilePath, List<Assembly> mappingAssemblies)
        {
            if (mappingAssemblies == null || mappingAssemblies.Count == 0)
            {
                throw new InfrastructureException("At least one assembly mapping must be supplied.");
            }

            if (!File.Exists(configFilePath))
            {
                throw new InfrastructureException(string.Format("Could not find the specified configfile: [{0}]", configFilePath));
            }

            // Load the config from file and do the configuration
            var cfg = LoadUnmappedConfig(configFilePath);

            // Configure the mappings
            Config = ConfigureAllMappingAssemblies(cfg, mappingAssemblies);
        }

        private static Configuration LoadUnmappedConfig(string configFile)
        {
            // Get configuration
            var cfg = new Configuration();
            cfg = cfg.Configure(configFile);
            return cfg;
        }

        private static Configuration ConfigureAllMappingAssemblies(Configuration cfg, IEnumerable<Assembly> mappingAssemblies)
        {
            foreach (var mappingAssembly in mappingAssemblies)
            {
                cfg = cfg.AddAssembly(mappingAssembly);
            }
            return cfg;
        }

        #endregion Create Configuration and mappings


        public static void ChangeConnectionInfoOnExistingConfiguration(string configFile, string database, string userName, string password)
        {
            // Change connectioninfo on existing configuration
            Config.Properties["connection.connection_string"] = string.Format("User ID={0};Password={1};Data Source={2}", userName, password, database);
        }


        public static void CreateCleanDataBaseSchema(bool showSql)
        {
            new SchemaExport(Config).Create(showSql, true);
        }


        public static void UpdateDataBaseSchema(bool showSql)
        {
            new SchemaUpdate(Config).Execute(showSql, true);
        }


        public static void ValidateDatabaseSchema()
        {
            new SchemaValidator(Config).Validate();
        }

        public static bool IsDatabaseSchemaUpToDate()
        {
            try
            {
                new SchemaValidator(Config).Validate();
            }
            catch
            {
                return false;
            }
            return true;
        }


        public static void CleanUp()
        {
            if (sessionFactory != null)
            {
                if (!sessionFactory.IsClosed)
                {
                    sessionFactory.Close();
                }

                sessionFactory = null;
            }

        }
    }
}
