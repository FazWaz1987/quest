﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AxeSoftware.Quest.Scripts;

namespace AxeSoftware.Quest
{
    public class EditableScriptData
    {
        public EditableScriptData(Element editor)
        {
            DisplayString = editor.Fields.GetString("display");
            Category = editor.Fields.GetString("category");
            CreateString = editor.Fields.GetString("create");
            AdderDisplayString = editor.Fields.GetString("add");
        }

        public string DisplayString { get; private set; }
        public string Category { get; private set; }
        public string CreateString { get; private set; }
        public string AdderDisplayString { get; private set; }
    }

    internal class EditableScriptFactory
    {
        private EditorController m_controller;
        private ScriptFactory m_scriptFactory;
        private WorldModel m_worldModel;
        private Dictionary<string, EditableScriptData> m_scriptData = new Dictionary<string, EditableScriptData>();

        internal EditableScriptFactory(EditorController controller, ScriptFactory factory, WorldModel worldModel)
        {
            m_controller = controller;
            m_scriptFactory = factory;
            m_worldModel = worldModel;

            foreach (Element editor in worldModel.Elements.GetElements(ElementType.Editor).Where(e => IsScriptEditor(e)))
            {
                string appliesTo = editor.Fields.GetString("appliesto");
                m_scriptData.Add(appliesTo, new EditableScriptData(editor));
            }
        }

        private bool IsScriptEditor(Element editor)
        {
            return !string.IsNullOrEmpty(editor.Fields.GetString("category"));
        }

        internal EditableScriptBase CreateEditableScript(string keyword, string parent)
        {
            return CreateEditableScript(keyword, (parent == null) ? null : m_worldModel.Elements.Get(parent));
        }

        internal EditableScriptBase CreateEditableScript(string keyword, Element parent)
        {
            IScript script = m_scriptFactory.CreateSimpleScript(keyword);
            return CreateEditableScript(script, parent);
        }

        internal EditableScriptBase CreateEditableScript(IScript script, Element parent)
        {
            EditableScriptBase newScript;
            IfScript ifScript = script as IfScript;
            if (ifScript != null)
            {
                newScript = new EditableIfScript(m_controller, ifScript, parent, m_worldModel.UndoLogger);
            }
            else
            {
                EditableScript newEditableScript = new EditableScript(script, m_worldModel.UndoLogger);
                if (script != null && m_scriptData.ContainsKey(script.Keyword)) newEditableScript.DisplayTemplate = m_scriptData[script.Keyword].DisplayString;
                newScript = newEditableScript;
            }
            return newScript;
        }

        internal IEnumerable<string> GetCategories()
        {
            List<string> result = new List<string>();
            foreach (EditableScriptData data in m_scriptData.Values)
            {
                if (!result.Contains(data.Category)) result.Add(data.Category);
            }
            return result;
        }

        internal Dictionary<string, EditableScriptData> ScriptData
        {
            get { return m_scriptData; }
        }
    }
}
