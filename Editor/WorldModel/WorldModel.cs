﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Threading;
using TextAdventures.Quest.Scripts;
using System.Linq;
using System.Net;
using TextAdventures.Quest.Functions;

namespace TextAdventures.Quest
{
    public enum GameState
    {
        NotStarted,
        Loading,
        Running,
        Finished
    }

    public enum ThreadState
    {
        Ready,
        Working,
        Waiting
    }

    public enum UpdateSource
    {
        System,
        User
    }

    public enum WorldModelVersion
    {
        v500,
        v510,
        v520,
        v530,
        v540,
        v550
    }

    public class ObjectsUpdatedEventArgs : EventArgs
    {
        public string Added { get; set; }
        public string Removed { get; set; }
    }

    public class DebugDataItem
    {
        public string Value
        {
            get;
            private set;
        }

        public bool IsInherited
        {
            get;
            set;
        }

        public string Source
        {
            get;
            set;
        }

        public bool IsDefaultType
        {
            get;
            set;
        }

        public DebugDataItem(string value)
            : this(value, false)
        {
        }

        public DebugDataItem(string value, bool isInherited)
            : this(value, isInherited, null)
        {
        }

        public DebugDataItem(string value, bool isInherited, string source)
        {
            Value = value;
            IsInherited = isInherited;
            Source = source;
        }
    }

    public class DebugData
    {
        private Dictionary<string, DebugDataItem> m_data = new Dictionary<string, DebugDataItem>();
        public Dictionary<string, DebugDataItem> Data
        {
            get { return m_data; }
            set { m_data = value; }
        }
    }

    public class WorldModel
    {
        private Element m_game;
        private Elements m_elements;
        private Dictionary<string, int> m_nextUniqueID = new Dictionary<string, int>();
        private Template m_template;
        private UndoLogger m_undoLogger;
        private string m_filename;
        private string m_originalFilename;
        private readonly string m_data;
        private string m_libFolder = null;
        private List<string> m_errors;
        private Dictionary<string, ObjectType> m_debuggerObjectTypes = new Dictionary<string, ObjectType>();
        private Dictionary<string, ElementType> m_debuggerElementTypes = new Dictionary<string, ElementType>();
        private GameState m_state = GameState.NotStarted;
        private Dictionary<ElementType, IElementFactory> m_elementFactories = new Dictionary<ElementType, IElementFactory>();
        private ObjectFactory m_objectFactory;
        private GameSaver m_saver;
        private string m_saveFilename = string.Empty;
        private bool m_editMode = false;
        private Functions.ExpressionOwner m_expressionOwner;
        private ThreadState m_threadState = ThreadState.Ready;
        private object m_threadLock = new object();
        private List<string> m_attributeNames = new List<string>();
        private RegexCache m_regexCache = new RegexCache();
        private CallbackManager m_callbacks = new CallbackManager();

        private static Dictionary<ObjectType, string> s_defaultTypeNames = new Dictionary<ObjectType, string>();
        private static Dictionary<string, Type> s_typeNamesToTypes = new Dictionary<string, Type>();
        private static Dictionary<Type, string> s_typesToTypeNames = new Dictionary<Type, string>();

        public event EventHandler<ElementFieldUpdatedEventArgs> ElementFieldUpdated;
        public event EventHandler<ElementRefreshEventArgs> ElementRefreshed;
        public event EventHandler<ElementFieldUpdatedEventArgs> ElementMetaFieldUpdated;
        public event EventHandler<LoadStatusEventArgs> LoadStatus;

        public class ElementFieldUpdatedEventArgs : EventArgs
        {
            internal ElementFieldUpdatedEventArgs(Element element, string attribute, object newValue, bool isUndo)
            {
                Element = element;
                Attribute = attribute;
                NewValue = newValue;
                IsUndo = isUndo;
            }

            public Element Element { get; private set; }
            public string Attribute { get; private set; }
            public object NewValue { get; private set; }
            public bool IsUndo { get; private set; }
            public bool Refresh { get; private set; }
        }

        public class ElementRefreshEventArgs : EventArgs
        {
            internal ElementRefreshEventArgs(Element element)
            {
                Element = element;
            }

            public Element Element { get; private set; }
        }

        public class LoadStatusEventArgs : EventArgs
        {
            public LoadStatusEventArgs(string status)
            {
                Status = status;
            }

            public string Status { get; private set; }
        }

        static WorldModel()
        {
            s_defaultTypeNames.Add(ObjectType.Object, "defaultobject");
            s_defaultTypeNames.Add(ObjectType.Exit, "defaultexit");
            s_defaultTypeNames.Add(ObjectType.Command, "defaultcommand");
            s_defaultTypeNames.Add(ObjectType.Game, "defaultgame");
            s_defaultTypeNames.Add(ObjectType.TurnScript, "defaultturnscript");

            s_typeNamesToTypes.Add("string", typeof(string));
            s_typeNamesToTypes.Add("script", typeof(IScript));
            s_typeNamesToTypes.Add("boolean", typeof(bool));
            s_typeNamesToTypes.Add("int", typeof(int));
            s_typeNamesToTypes.Add("double", typeof(double));
            s_typeNamesToTypes.Add("object", typeof(Element));
            s_typeNamesToTypes.Add("stringlist", typeof(QuestList<string>));
            s_typeNamesToTypes.Add("objectlist", typeof(QuestList<Element>));
            s_typeNamesToTypes.Add("stringdictionary", typeof(QuestDictionary<string>));
            s_typeNamesToTypes.Add("objectdictionary", typeof(QuestDictionary<Element>));
            s_typeNamesToTypes.Add("scriptdictionary", typeof(QuestDictionary<IScript>));
            s_typeNamesToTypes.Add("dictionary", typeof(QuestDictionary<object>));
            s_typeNamesToTypes.Add("list", typeof (QuestList<object>));

            foreach (KeyValuePair<string, Type> kvp in s_typeNamesToTypes)
            {
                s_typesToTypeNames.Add(kvp.Value, kvp.Key);
            }
        }

        public WorldModel()
            : this(null, null)
        {
        }

        public WorldModel(string data)
            : this(null, null)
        {
            m_data = data;
        }

        public WorldModel(string filename, string originalFilename)
        {
            m_expressionOwner = new Functions.ExpressionOwner(this);
            m_template = new Template(this);
            InitialiseElementFactories();
            m_objectFactory = (ObjectFactory)m_elementFactories[ElementType.Object];

            InitialiseDebuggerObjectTypes();
            m_filename = filename;
            m_originalFilename = originalFilename;
            m_elements = new Elements();
            m_undoLogger = new UndoLogger(this);
            m_game = ObjectFactory.CreateObject("game", ObjectType.Game);
        }

        public WorldModel(string filename, string libFolder, string originalFilename)
            : this(filename, originalFilename)
        {
            m_libFolder = libFolder;
        }

        private void InitialiseElementFactories()
        {
            foreach (Type t in TextAdventures.Utility.Classes.GetImplementations(System.Reflection.Assembly.GetExecutingAssembly(),
                typeof(IElementFactory)))
            {
                AddElementFactory((IElementFactory)Activator.CreateInstance(t));
            }
        }

        private void AddElementFactory(IElementFactory factory)
        {
            m_elementFactories.Add(factory.CreateElementType, factory);
            factory.WorldModel = this;
            factory.ObjectsUpdated += ElementsUpdated;
        }

        internal static Dictionary<ObjectType, string> DefaultTypeNames
        {
            get { return s_defaultTypeNames; }
        }

        void ElementsUpdated(object sender, ObjectsUpdatedEventArgs args)
        {
            if (ObjectsUpdated != null) ObjectsUpdated(this, args);
        }

        private void InitialiseDebuggerObjectTypes()
        {
            m_debuggerObjectTypes.Add("Objects", ObjectType.Object);
            m_debuggerObjectTypes.Add("Exits", ObjectType.Exit);
            m_debuggerObjectTypes.Add("Commands", ObjectType.Command);
            m_debuggerObjectTypes.Add("Game", ObjectType.Game);
            m_debuggerObjectTypes.Add("Turn Scripts", ObjectType.TurnScript);
            m_debuggerElementTypes.Add("Timers", ElementType.Timer);
        }

        internal string GetUniqueID()
        {
            return GetUniqueID(null);
        }

        internal string GetUniqueID(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) prefix = "k";
            if (!m_nextUniqueID.ContainsKey(prefix))
            {
                m_nextUniqueID.Add(prefix, 0);
            }

            string newid;
            do
            {
                m_nextUniqueID[prefix]++;
                newid = prefix + m_nextUniqueID[prefix].ToString();
            } while (m_elements.ContainsKey(newid));
            
            return newid;
        }

        public Element Game
        {
            get { return m_game; }
        }

        public Element Object(string name)
        {
            return m_elements.Get(ElementType.Object, name);
        }

        public ObjectFactory ObjectFactory
        {
            get { return m_objectFactory; }
        }

        public IElementFactory GetElementFactory(ElementType t)
        {
            return m_elementFactories[t];
        }

        public void PrintTemplate(string t)
        {
            Print(m_template.GetText(t));
        }

        public void Print(string text, bool linebreak = true)
        {
            if (Version >= WorldModelVersion.v540 && m_elements.ContainsKey(ElementType.Function, "OutputText"))
            {
                try
                {
                    RunProcedure("OutputText", new Parameters(new Dictionary<string, string> {{"text", text}}), false);
                }
                catch (Exception ex)
                {
                    LogException(ex);
                }
            }
            else
            {
                if (PrintText != null)
                {
                    if (linebreak)
                    {
                        PrintText("<output>" + text + "</output>");
                    }
                    else
                    {
                        PrintText("<output nobr=\"true\">" + text + "</output>");
                    }
                }
            }
        }

        internal QuestList<Element> GetAllObjects()
        {
            return new QuestList<Element>(m_elements.Objects);
        }

        internal QuestList<Element> GetObjectsInScope(string scopeFunction)
        {
            if (m_elements.ContainsKey(ElementType.Function, scopeFunction))
            {
                return (QuestList<Element>)RunProcedure(scopeFunction, true);
            }
            throw new Exception(string.Format("No function '{0}'", scopeFunction));
        }

        public bool ObjectContains(Element parent, Element searchObj)
        {
            if (searchObj.Parent == null) return false;
            if (searchObj.Parent == parent) return true;
            return ObjectContains(parent, searchObj.Parent);
        }

        public IEnumerable<Element> Objects
        {
            get
            {
                foreach (Element o in m_elements.Objects)
                    yield return o;
            }
        }

        public bool ObjectExists(string name)
        {
            return m_elements.ContainsKey(ElementType.Object, name);
        }

        /// <summary>
        /// Attempt to resolve an element name from elements which are eligible for expression,
        /// i.e. objects and timers
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public bool TryResolveExpressionElement(string name, out Element element)
        {
            element = null;
            if (!m_elements.ContainsKey(name)) return false;

            Element result = m_elements.Get(name);
            if (result.ElemType == ElementType.Object || result.ElemType == ElementType.Timer)
            {
                element = result;
                return true;
            }

            return false;
        }

        internal void RemoveElement(ElementType type, string name)
        {
            m_elements.Remove(type, name);
        }

        internal IEnumerable<Element> ElementTypes
        {
            get { return m_elements.GetElements(ElementType.ObjectType); }
        }

        public bool InitialiseEdit()
        {
            m_editMode = true;
            GameLoader loader = new GameLoader(this, GameLoader.LoadMode.Edit);
            return InitialiseInternal(loader);
        }

        private bool InitialiseInternal(GameLoader loader)
        {
            if (m_state != GameState.NotStarted)
            {
                throw new Exception("Game already initialised");
            }
            loader.FilenameUpdated += loader_FilenameUpdated;
            loader.LoadStatus += loader_LoadStatus;
            m_state = GameState.Loading;
            
            bool success;
            if (m_data != null)
            {
                success = loader.Load(data: m_data);
            }
            else
            {
                success = m_filename == null || loader.Load(m_filename);    
            }
            
            DebugEnabled = !loader.IsCompiledFile;
            m_state = success ? GameState.Running : GameState.Finished;
            m_errors = loader.Errors;
            m_saver = new GameSaver(this);
            return success;
        }

        void loader_LoadStatus(object sender, GameLoader.LoadStatusEventArgs e)
        {
            if (LoadStatus != null)
            {
                LoadStatus(this, new LoadStatusEventArgs(e.Status));
            }
        }

        void loader_FilenameUpdated(string filename)
        {
            // Update base ASLX filename to original filename if we're loading a saved game
            m_saveFilename = m_filename;
            m_filename = filename;
        }

        public List<string> Errors
        {
            get { return m_errors; }
        }

        public List<string> DebuggerObjectTypes
        {
            get { return new List<string>(m_debuggerObjectTypes.Keys.Union(m_debuggerElementTypes.Keys)); }
        }

        public string Filename
        {
            get { return m_filename; }
        }

        public string SaveFilename
        {
            get { return m_saveFilename; }
        }

        public string BasePath
        {
            get { return System.IO.Path.GetDirectoryName(m_filename); }
        }

        public string SaveExtension { get { return "quest-save"; } }

        public event PrintTextHandler PrintText;
        public event UpdateListHandler UpdateList;
        public event EventHandler<ObjectsUpdatedEventArgs> ObjectsUpdated;
        public event ErrorHandler LogError;

        internal Template Template
        {
            get { return m_template; }
        }

        public UndoLogger UndoLogger
        {
            get { return m_undoLogger; }
        }

        private void UpdateStatusVariables()
        {
            if (m_elements.ContainsKey(ElementType.Function, "UpdateStatusAttributes"))
            {
                try
                {
                    RunProcedure("UpdateStatusAttributes");
                }
                catch (Exception ex)
                {
                    LogException(ex);
                }
            }
        }

        internal void UpdateLists()
        {
            UpdateObjectsList();
            UpdateExitsList();
            UpdateStatusVariables();
        }

        internal void UpdateObjectsList()
        {
            UpdateObjectsList("GetPlacesObjectsList", ListType.ObjectsList);
            UpdateObjectsList("ScopeInventory", ListType.InventoryList);
        }

        internal void UpdateObjectsList(string scope, ListType listType)
        {
            if (UpdateList != null)
            {
                List<ListData> objects = new List<ListData>();
                foreach (Element obj in GetObjectsInScope(scope))
                {
                    if (Version <= WorldModelVersion.v520 || !m_elements.ContainsKey(ElementType.Function, "GetDisplayVerbs"))
                    {
                        if (scope == "ScopeInventory")
                        {
                            objects.Add(new ListData(GetListDisplayAlias(obj), obj.Fields[FieldDefinitions.InventoryVerbs], obj.Name, GetDisplayAlias(obj)));
                        }
                        else
                        {
                            objects.Add(new ListData(GetListDisplayAlias(obj), obj.Fields[FieldDefinitions.DisplayVerbs], obj.Name, GetDisplayAlias(obj)));
                        }
                    }
                    else
                    {
                        objects.Add(new ListData(GetListDisplayAlias(obj), GetDisplayVerbs(obj), obj.Name, GetDisplayAlias(obj)));
                    }
                }
                // The "Places and Objects" list is generated by function, so we also
                // need to add any exits. (The UI is responsible for filtering out the
                // directional exits so they only display in the compass)
                if (scope == "GetPlacesObjectsList") objects.AddRange(GetExitsListData());
                UpdateList(listType, objects);
            }
        }

        internal void UpdateExitsList()
        {
            if (UpdateList != null)
            {
                UpdateList(ListType.ExitsList, GetExitsListData());
            }
        }

        private string GetListDisplayAlias(Element obj)
        {
            if (m_elements.ContainsKey(ElementType.Function, "GetListDisplayAlias"))
            {
                return (string)RunProcedure("GetListDisplayAlias", new Parameters("obj", obj), true);
            }
            return GetDisplayAlias(obj);
        }

        private string GetDisplayAlias(Element obj)
        {
            if (m_elements.ContainsKey(ElementType.Function, "GetDisplayAlias"))
            {
                return (string)RunProcedure("GetDisplayAlias", new Parameters("obj", obj), true);
            }
            return obj.Name;
        }

        private IEnumerable<string> GetDisplayVerbs(Element obj)
        {
            return (QuestList<string>)RunProcedure("GetDisplayVerbs", new Parameters("object", obj), true);
        }

        private List<ListData> GetExitsListData()
        {
            List<ListData> exits = new List<ListData>();
            var scopeFunction = "ScopeExits";
            if (Version >= WorldModelVersion.v530 && Elements.ContainsKey(ElementType.Function, "GetExitsList"))
            {
                scopeFunction = "GetExitsList";
            }
            foreach (Element exit in GetObjectsInScope(scopeFunction))
            {
                IEnumerable<string> verbs;
                if (Version <= WorldModelVersion.v520 || !m_elements.ContainsKey(ElementType.Function, "GetDisplayVerbs"))
                {
                    verbs = exit.Fields[FieldDefinitions.DisplayVerbs];
                }
                else
                {
                    verbs = GetDisplayVerbs(exit);
                }
                exits.Add(new ListData(GetListDisplayAlias(exit), verbs, exit.Name, GetDisplayAlias(exit)));
            }
            return exits;
        }

        public List<string> GetObjects(string type)
        {
            List<string> result = new List<string>();
            IEnumerable<Element> elements;

            if (m_debuggerObjectTypes.ContainsKey(type))
            {
                ObjectType filterType = m_debuggerObjectTypes[type];
                elements = m_elements.ObjectsFiltered(o => o.Type == filterType);
            }
            else
            {
                ElementType filterType = m_debuggerElementTypes[type];
                elements = m_elements.GetElements(filterType);
            }

            foreach (Element obj in elements)
            {
                result.Add(obj.Name);
            }

            return result;
        }

        public DebugData GetDebugData(string el)
        {
            return m_elements.Get(el).GetDebugData();
        }

        public DebugData GetInheritedTypesDebugData(string el)
        {
            return m_elements.Get(el).Fields.GetInheritedTypesDebugData();
        }

        public DebugDataItem GetDebugDataItem(string el, string attribute)
        {
            return m_elements.Get(el).Fields.GetDebugDataItem(attribute);
        }

        public IEnumerable<string> GetExternalScripts()
        {
            var result = new List<string>();
            foreach (Element jsRef in m_elements.GetElements(ElementType.Javascript))
            {
                if (Version == WorldModelVersion.v500)
                {
                    // v500 games used Frame.js functions for static panel feature. This is now implemented natively
                    // in Player and WebPlayer.
                    if (jsRef.Fields[FieldDefinitions.Src].ToLower() == "frame.js") continue;
                }

                result.Add(jsRef.Fields[FieldDefinitions.Src]);
            }
            return result;
        }

        // TO DO: This could actually be removed now, as we can dynamically load stylesheets. Core.aslx InitInterface
        // should simply be able to use the SetWebFontName function to load game.defaultwebfont
        public IEnumerable<string> GetExternalStylesheets()
        {
            if (Version < WorldModelVersion.v530) return null;

            var webFontsInUse = new List<string>();
            var defaultWebFont = m_game.Fields[FieldDefinitions.DefaultWebFont];
            if (!string.IsNullOrEmpty(defaultWebFont))
            {
                webFontsInUse.Add(defaultWebFont);
            }
            
            var result = webFontsInUse.Select(f => "https://fonts.googleapis.com/css?family=" + f.Replace(' ', '+'));
            
            return result;
        }

        public void RunScript(IScript script)
        {
            RunScript(script, (Parameters)null, false);
        }

        /// <summary>
        /// Use this version of RunScript when executing an object action. Set thisElement to the object whose action it is.
        /// </summary>
        /// <param name="script"></param>
        /// <param name="thisElement"></param>
        public void RunScript(IScript script, Element thisElement)
        {
            RunScript(script, null, false, thisElement);
        }

        public void RunScript(IScript script, Parameters parameters)
        {
            RunScript(script, parameters, false);
        }

        public void RunScript(IScript script, Parameters parameters, Element thisElement)
        {
            RunScript(script, parameters, false, thisElement);
        }

        public object RunDelegateScript(IScript script, Parameters parameters, Element thisElement)
        {
            return RunScript(script, parameters, true, thisElement);
        }

        private object RunScript(IScript script, Parameters parameters, bool expectResult)
        {
            return RunScript(script, parameters, expectResult, null);
        }

        private object RunScript(IScript script, Parameters parameters, bool expectResult, Element thisElement)
        {
            Context c = new Context();
            if (parameters == null) parameters = new Parameters();
            if (thisElement != null) parameters.Add("this", thisElement);
            c.Parameters = parameters;

            return RunScript(script, c, expectResult);
        }

        private void RunScript(IScript script, Context c)
        {
            RunScript(script, c, false);
        }

        private object RunScript(IScript script, Context c, bool expectResult)
        {
            try
            {
                script.Execute(c);
                if (expectResult && c.ReturnValue is NoReturnValue) throw new Exception("Function did not return a value");
                return c.ReturnValue;
            }
            catch (Exception ex)
            {
                Print("Error running script: " + Utility.SafeXML(ex.Message));
            }
            return null;
        }

        public Element AddProcedure(string name)
        {
            Element proc = GetElementFactory(ElementType.Function).Create(name);
            return proc;
        }

        public Element AddProcedure(string name, IScript script, string[] parameters)
        {
            Element proc = AddProcedure(name);
            proc.Fields[FieldDefinitions.Script] = script;
            proc.Fields[FieldDefinitions.ParamNames] = new QuestList<string>(parameters);
            return proc;
        }

        public Element AddDelegate(string name)
        {
            Element del = GetElementFactory(ElementType.Delegate).Create(name);
            return del;
        }

        public void RunProcedure(string name)
        {
            RunProcedure(name, false);
        }

        public object RunProcedure(string name, bool expectResult)
        {
            return RunProcedure(name, null, expectResult);
        }

        public object RunProcedure(string name, Parameters parameters, bool expectResult)
        {
            if (m_elements.ContainsKey(ElementType.Function, name))
            {
                Element function = m_elements.Get(ElementType.Function, name);

                // Only check for too few parameters for games for Quest 5.2 or later, as previous Quest versions
                // would ignore this (but would usually still fail when the function was run, as the required
                // variable wouldn't exist). For Quest 5.3, an additional check if parameters is non-null but empty.

                bool parametersInvalid = false;
                if (Version == WorldModelVersion.v520)
                {
                    parametersInvalid = parameters == null && function.Fields[FieldDefinitions.ParamNames].Count > 0;
                }
                else if (Version >= WorldModelVersion.v530)
                {
                    parametersInvalid = (parameters == null || parameters.Count == 0) && function.Fields[FieldDefinitions.ParamNames].Count > 0;
                }

                if (parametersInvalid)
                {
                    throw new Exception(string.Format("No parameters passed to {0} function - expected {1} parameters",
                            name,
                            function.Fields[FieldDefinitions.ParamNames].Count));
                }

                return RunScript(function.Fields[FieldDefinitions.Script], parameters, expectResult);
            }
            else
            {
                Print(string.Format("Error - no such procedure '{0}'", name));
            }
            return null;
        }

        public Element Procedure(string name)
        {
            if (!m_elements.ContainsKey(ElementType.Function, name)) return null;
            return m_elements.Get(ElementType.Function, name);
        }

        internal Element GetObjectType(string name)
        {
            return m_elements.Get(ElementType.ObjectType, name);
        }

        public GameState State
        {
            get { return m_state; }
        }

        public Elements Elements
        {
            get { return m_elements; }
        }

        public void Save(string filename, string html)
        {
            string saveData = Save(SaveMode.SavedGame, html: html);
            File.WriteAllText(filename, saveData);
        }

        public byte[] Save(string html)
        {
            string saveData = Save(SaveMode.SavedGame, html: html);
            return System.Text.Encoding.UTF8.GetBytes(saveData);
        }

        public string Save(SaveMode mode, bool? includeWalkthrough = null, string html = null)
        {
            return m_saver.Save(mode, includeWalkthrough, html);
        }

        public static Type ConvertTypeNameToType(string name)
        {
            Type type;
            if (s_typeNamesToTypes.TryGetValue(name, out type))
            {
                return type;
            }

            if (name == "null") return null;

            // TO DO: type name could also be a DelegateImplementation
            //if (value is DelegateImplementation) return ((DelegateImplementation)value).TypeName;

            throw new ArgumentOutOfRangeException(string.Format("Unrecognised type name '{0}'", name));
        }

        public static string ConvertTypeToTypeName(Type type)
        {
            string name;
            if (s_typesToTypeNames.TryGetValue(type, out name))
            {
                return name;
            }

            foreach (KeyValuePair<Type, string> kvp in s_typesToTypeNames)
            {
                if (kvp.Key.IsAssignableFrom(type))
                {
                    return kvp.Value;
                }
            }

            throw new ArgumentOutOfRangeException(string.Format("Unrecognised type '{0}'", type.ToString()));
        }

        public string GetExternalPath(string file)
        {
            return GetExternalPath(file, true);
        }

        private string TryGetExternalPath(string file)
        {
            return GetExternalPath(file, false);
        }

        private string GetExternalPath(string file, bool throwException)
        {
            string resourcesFolder = ResourcesFolder ?? Path.GetDirectoryName(Filename);
            return GetExternalPath(resourcesFolder, file, throwException);
        }

        private string GetExternalPath(string current, string file, bool throwException)
        {
            string path;

            if (TryPath(current, file, out path, false)) return path;
            if (ResourcesFolder == null)
            {
                // Only try other folders if we're not using a resource folder (i.e. a .quest file)
                // Because if we do have a resource folder, all required external files should be there.

                if (TryPath(Environment.CurrentDirectory, file, out path, false)) return path;
                if (!string.IsNullOrEmpty(m_libFolder) && TryPath(m_libFolder, file, out path, true)) return path;
                if (System.Reflection.Assembly.GetEntryAssembly() != null)
                {
                    if (TryPath(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().CodeBase), file, out path, true)) return path;
                }
            }
            if (throwException)
            {
                throw new Exception(
                    string.Format("Cannot find a file called '{0}' in current path or application/resource path", file));
            }
            return null;
        }

        public IEnumerable<string> GetAvailableLibraries()
        {
            List<string> result = new List<string>();
            AddFilesInPathToList(result, System.IO.Path.GetDirectoryName(Filename), false);
            AddFilesInPathToList(result, Environment.CurrentDirectory, false);
            if (m_libFolder != null) AddFilesInPathToList(result, m_libFolder, false);
            if (System.Reflection.Assembly.GetEntryAssembly() != null)
            {
                AddFilesInPathToList(result, System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().CodeBase), true);
            }
            return result;
        }

        private void AddFilesInPathToList(List<string> list, string path, bool recurse, string searchPattern = "*.aslx")
        {
            path = TextAdventures.Utility.Utility.RemoveFileColonPrefix(path);
            System.IO.SearchOption option = recurse ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly;
            foreach (var result in System.IO.Directory.GetFiles(path, searchPattern, option))
            {
                if (result == Filename) continue;
                string filename = System.IO.Path.GetFileName(result);
                if (!list.Contains(filename)) list.Add(filename);
            }
        }

        public IEnumerable<string> GetAvailableExternalFiles(string searchPatterns)
        {
            List<string> result = new List<string>();
            string[] patterns = searchPatterns.Split(';');
            foreach (string searchPattern in patterns)
            {
                AddFilesInPathToList(result, System.IO.Path.GetDirectoryName(Filename), false, searchPattern);
            }
            return result;
        }

        private bool TryPath(string path, string file, out string fullPath, bool recurse)
        {
            path = TextAdventures.Utility.Utility.RemoveFileColonPrefix(path);
            fullPath = System.IO.Path.Combine(path, file);
            if (System.IO.File.Exists(fullPath))
            {
                return true;
            }
            else
            {
                if (recurse && !file.Contains("\\") && !file.Contains("/"))
                {
                    var results = System.IO.Directory.GetFiles(path, file, System.IO.SearchOption.AllDirectories);
                    if (results.Length > 0)
                    {
                        fullPath = results[0];
                        return true;
                    }
                }
                return false;
            }
        }

        internal void NotifyElementFieldUpdate(Element element, string attribute, object newValue, bool isUndo)
        {
            if (!element.Initialised) return;
            if (ElementFieldUpdated != null) ElementFieldUpdated(this, new ElementFieldUpdatedEventArgs(element, attribute, newValue, isUndo));
        }

        internal void NotifyElementMetaFieldUpdate(Element element, string attribute, object newValue, bool isUndo)
        {
            if (!element.Initialised) return;
            if (ElementMetaFieldUpdated != null) ElementMetaFieldUpdated(this, new ElementFieldUpdatedEventArgs(element, attribute, newValue, isUndo));
        }

        internal void NotifyElementRefreshed(Element element)
        {
            if (ElementRefreshed != null) ElementRefreshed(this, new ElementRefreshEventArgs(element));
        }

        public bool EditMode
        {
            get { return m_editMode; }
        }

        internal Functions.ExpressionOwner ExpressionOwner
        {
            get { return m_expressionOwner; }
        }

        private void ChangeThreadState(ThreadState newState, bool scroll = true)
        {
            if (newState == ThreadState.Waiting && m_state == GameState.Finished) throw new Exception("Game is finished");
            m_threadState = newState;
            lock (m_threadLock)
            {
                Monitor.PulseAll(m_threadLock);
            }
        }

        private void WaitUntilFinishedWorking()
        {
            lock (m_threadLock)
            {
                while (m_threadState == ThreadState.Working)
                {
                    Monitor.Wait(m_threadLock);
                }
            }
        }

        private void DoInNewThreadAndWait(Action routine)
        {
            Action wrappedRoutine = () =>
            {
                try
                {
                    routine();
                }
                catch { }
            };

            ChangeThreadState(ThreadState.Working);
            Thread newThread = new Thread(new ThreadStart(wrappedRoutine));
            newThread.Start();
            WaitUntilFinishedWorking();
        }

        void LogException(Exception ex)
        {
            if (LogError != null) LogError(ex.Message + Environment.NewLine + ex.StackTrace);
        }

        public ElementType GetElementTypeForTypeString(string typeString)
        {
            return Element.GetElementTypeForTypeString(typeString);
        }

        public ObjectType GetObjectTypeForTypeString(string typeString)
        {
            return Element.GetObjectTypeForTypeString(typeString);
        }

        public string GetTypeStringForElementType(ElementType type)
        {
            return Element.GetTypeStringForElementType(type);
        }

        public string GetTypeStringForObjectType(ObjectType type)
        {
            return Element.GetTypeStringForObjectType(type);
        }

        public bool IsDefaultTypeName(string typeName)
        {
            return DefaultTypeNames.ContainsValue(typeName);
        }

        public string OriginalFilename
        {
            get { return m_originalFilename; }
        }

        public Element AddNewTemplate(string templateName)
        {
            return m_template.AddTemplate(templateName, string.Empty, false);
        }

        public Element TryGetTemplateElement(string templateName)
        {
            if (!m_template.TemplateExists(templateName)) return null;
            return m_template.GetTemplateElement(templateName);
        }

        private static System.Text.RegularExpressions.Regex s_removeTrailingDigits = new System.Text.RegularExpressions.Regex(@"\d*$");

        public string GetUniqueElementName(string elementName)
        {
            // If element name doesn't exist (element might have been Cut in the editor),
            // then just return the original name
            if (!Elements.ContainsKey(elementName))
            {
                return elementName;
            }

            // Otherwise get a uniquely numbered element
            string root = s_removeTrailingDigits.Replace(elementName, "");
            bool elementAlreadyExists = true;
            int number = 0;
            string result = null;

            while (elementAlreadyExists)
            {
                number++;
                result = root + number.ToString();
                elementAlreadyExists = Elements.ContainsKey(result);
            }

            return result;
        }

        internal void AddAttributeName(string name)
        {
            if (!m_attributeNames.Contains(name)) m_attributeNames.Add(name);
        }

        public IEnumerable<string> GetAllAttributeNames
        {
            get { return m_attributeNames.AsReadOnly(); }
        }

        public class PackageIncludeFile
        {
            public string Filename { get; set; }
            public Stream Content { get; set; }
        }

        public bool CreatePackage(string filename, bool includeWalkthrough, out string error, IEnumerable<PackageIncludeFile> includeFiles, Stream outputStream)
        {
            Packager packager = new Packager(this);
            return packager.CreatePackage(filename, includeWalkthrough, out error, includeFiles, outputStream);
        }

        public string ResourcesFolder { get; internal set; }
        public bool DebugEnabled { get; private set; }

        private static List<string> s_functionNames = null;

        public IEnumerable<string> GetBuiltInFunctionNames()
        {
            if (s_functionNames == null)
            {
                System.Reflection.MethodInfo[] methods = typeof(ExpressionOwner).GetMethods();
                System.Reflection.MethodInfo[] stringMethods = typeof(StringFunctions).GetMethods();

                IEnumerable<System.Reflection.MethodInfo> allMethods = methods.Union(stringMethods);

                s_functionNames = new List<string>(allMethods.Select(m => m.Name));
            }

            return s_functionNames.AsReadOnly();
        }

        internal void UpdateElementSortOrder(Element movedElement)
        {
            // There's no need to worry about element sort order when playing the game, unless this is an element that can be seen by the
            // player
            if (!EditMode)
            {
                bool doUpdate = false;
                if (movedElement.ElemType == ElementType.Object && (movedElement.Type == ObjectType.Exit || movedElement.Type == ObjectType.Object))
                {
                    doUpdate = true;
                }
                if (!doUpdate) return;
            }

            // This function is called when an element is moved to a new parent.
            // When this happens, its SortIndex MetaField must be updated so that it
            // is at the end of the list of children.

            int maxIndex = -1;

            foreach (Element sibling in m_elements.GetDirectChildren(movedElement.Parent))
            {
                int thisSortIndex = sibling.MetaFields[MetaFieldDefinitions.SortIndex];
                if (thisSortIndex > maxIndex) maxIndex = thisSortIndex;
            }

            movedElement.MetaFields[MetaFieldDefinitions.SortIndex] = maxIndex + 1;
        }

        public bool Assert(string expr)
        {
            Expression<bool> expression = new Expression<bool>(expr, new ScriptContext(this));
            Context c = new Context();
            return expression.Execute(c);
        }

        internal void AddOnReady(IScript callback, Context c)
        {
            if (!m_callbacks.AnyOutstanding())
            {
                RunScript(callback, c);
            }
            else
            {
                m_callbacks.AddOnReadyCallback(new Callback(callback, c));
            }
        }

        public Stream GetResource(string filename)
        {
            string path = TryGetExternalPath(filename);
            if (path == null) return null;
            return new FileStream(GetExternalPath(filename), FileMode.Open, FileAccess.Read);
        }

        public string GetResourcePath(string filename)
        {
            return TryGetExternalPath(filename);
        }

        internal RegexCache RegexCache { get { return m_regexCache; } }

        ~WorldModel()
        {
            if (ResourcesFolder != null && System.IO.Directory.Exists(ResourcesFolder))
            {
                try
                {
                    System.IO.Directory.Delete(ResourcesFolder, true);
                }
                catch { }
            }
        }

        public WorldModelVersion Version { get; internal set; }

        internal string VersionString { get; set; }

        public string TempFolder { get; set; }

        public int ASLVersion { get { return int.Parse(VersionString); } }

        public string GameID
        {
            get
            {
                string gameId = m_game.Fields[FieldDefinitions.GameID];
                if (gameId != null) return gameId;
                if (Config.ReadGameFileFromAzureBlob)
                {
                    var parts = m_filename.Split('/');
                    return parts[parts.Length - 2];
                }
                return TextAdventures.Utility.Utility.FileMD5Hash(m_filename);
            }
        }
        public string Category { get { return m_game.Fields[FieldDefinitions.Category]; } }
        public string Description { get { return m_game.Fields[FieldDefinitions.Description]; } }
        public string Cover { get { return m_game.Fields[FieldDefinitions.Cover]; } }
    }

    // ************ TODO: Get rid of this.... *************************************************

    public delegate void PrintTextHandler(string text);
    public delegate void UpdateListHandler(ListType listType, List<ListData> items);
    public delegate void FinishedHandler();
    public delegate void ErrorHandler(string errorMessage);

    public enum ListType
    {
        InventoryList,
        ExitsList,
        ObjectsList
    }

    public enum UIOption
    {
        UseGameColours,
        UseGameFont,
        OverrideForeground,
        OverrideLinkForeground,
        OverrideFontName,
        OverrideFontSize
    }

    public class MenuData
    {
        private string m_caption;
        private IDictionary<string, string> m_options;
        private bool m_allowCancel;

        public MenuData(string caption, IDictionary<string, string> options, bool allowCancel)
        {
            m_caption = caption;
            m_options = options;
            m_allowCancel = allowCancel;
        }

        public string Caption
        {
            get { return m_caption; }
        }

        public IDictionary<string, string> Options
        {
            get { return m_options; }
        }

        public bool AllowCancel
        {
            get { return m_allowCancel; }
        }
    }

    public class ListData
    {
        private string m_text;
        private IEnumerable<string> m_verbs;
        private string m_elementId;
        private string m_elementName;

        public ListData(string text, IEnumerable<string> verbs)
            : this(text, verbs, null, text)
        {
        }

        public ListData(string text, IEnumerable<string> verbs, string elementId, string elementName)
        {
            m_text = text;
            m_verbs = verbs;
            m_elementId = elementId;
            m_elementName = elementName;
        }

        public string Text
        {
            get { return m_text; }
        }

        public IEnumerable<string> Verbs
        {
            get { return m_verbs; }
        }

        public string ElementId
        {
            get { return m_elementId; }
        }

        public string ElementName
        {
            get { return m_elementName; }
        }
    }

    // ************ END TODO: Get rid of this.... *************************************************
}