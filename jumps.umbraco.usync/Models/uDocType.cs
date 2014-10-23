﻿using jumps.umbraco.usync.helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using umbraco.cms.businesslogic.web;
using Umbraco.Core;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;

namespace jumps.umbraco.usync.Models
{
    public static class uDocType
    {
        public static XElement SyncExport(this DocumentType item)
        {
            XmlDocument xmlDoc = helpers.XmlDoc.CreateDoc();
            xmlDoc.AppendChild(item.ToXml(xmlDoc));

            var node = XElement.Load(new XmlNodeReader(xmlDoc));
            node = FixProperies(item, node);
            node = TabSortOrder(item, node);

            return node;
        }

        private static XElement FixProperies(DocumentType item, XElement node)
        {
            var props = node.Element("GenericProperties");

            if (props == null)
                return node;
            props.RemoveAll();

            foreach(var property in item.PropertyTypes.OrderBy(x => x.Name))
            {
                XElement prop = new XElement("GenericProperty");

                prop.Add(new XElement("Name", property.Name));
                prop.Add(new XElement("Alias", property.Alias));
                prop.Add(new XElement("Type", property.DataTypeDefinition.DataType.Id.ToString()));
                prop.Add(new XElement("Definition", property.DataTypeDefinition.UniqueId.ToString()));

                var tab = item.PropertyTypeGroups.Where(x => x.Id == property.PropertyTypeGroup ).FirstOrDefault();
                if (tab != null)
                    prop.Add(new XElement("Tab",tab.Name));

                prop.Add(new XElement("Mandatory", property.Mandatory));
                prop.Add(new XElement("Validation", property.ValidationRegExp));
                prop.Add(new XElement("Description", new XCData(property.Description)));
                prop.Add(new XElement("SortOrder", property.SortOrder));

                props.Add(prop);
            }
            return node;
        }

        private static XElement TabSortOrder(DocumentType item, XElement node)
        {
            var tabNode = node.Element("Tabs");

            if ( tabNode != null )
            {
                tabNode.RemoveAll();
            }
            foreach(var tab in item.PropertyTypeGroups.OrderBy(x => x.SortOrder))
            {
                var t = new XElement("Tab");
                t.Add(new XElement("Id", tab.Id));
                t.Add(new XElement("Caption", tab.Name));
                t.Add(new XElement("SortOrder", tab.SortOrder));

                tabNode.Add(t);
            }

            return node;
        }
        

        public static ChangeItem SyncImport(XElement node, bool postCheck = true)
        {
            var change = new ChangeItem
            {
                itemType = ItemType.DocumentType,
                changeType = ChangeType.Success,
                name = node.Element("Info").Element("Name").Value
            };

            // LogHelper.Info<uSync>("Import:\n {0}", () => node.ToString());
            LogHelper.Info<uSync>("Importing: {0}", () => node.Element("Info").Element("Name").Value);

            ApplicationContext.Current.Services.PackagingService.ImportContentTypes(node, false);

            return change;
        }

        public static ChangeItem SyncImportFitAndFix(IContentType item, XElement node, bool postCheck = true)
        {
            var change = new ChangeItem
            {
                itemType = ItemType.DocumentType,
                changeType = ChangeType.Success,
            };

            if ( item != null )
            {
                change.id = item.Id;
                change.name = item.Name;

                // basic stuff (like name)
                item.Description = node.Element("Info").Element("Description").Value;
                item.Thumbnail = node.Element("Info").Element("Thumbnail").Value;

                ImportStructure(item, node);

                RemoveMissingProperties(item, node);

                // tab sort order
                TabSortOrder(item, node);

                UpdateExistingProperties(item, node);

                ApplicationContext.Current.Services.ContentTypeService.Save(item);

                if ( postCheck && tracker.DocTypeChanged(node) )
                {
                    change.changeType = ChangeType.Mismatch;
                }

            }

            return change; 
        }


        private static void ImportStructure(IContentType docType, XElement node)
        {
            XElement structure = node.Element("Structure");

            List<ContentTypeSort> allowed = new List<ContentTypeSort>();
            int sortOrder = 0;

            foreach (var doc in structure.Elements("DocumentType"))
            {
                string alias = doc.Value;
                IContentType aliasDoc = ApplicationContext.Current.Services.ContentTypeService.GetContentType(alias);

                if (aliasDoc != null)
                {
                    allowed.Add(new ContentTypeSort(new Lazy<int>(() => aliasDoc.Id), sortOrder, aliasDoc.Name));
                    sortOrder++;
                }
            }

            docType.AllowedContentTypes = allowed;
        }

        private static void TabSortOrder(IContentType docType, XElement node)
        {
            XElement tabs = node.Element("Tabs");

            if (tabs == null)
                return;

            foreach (var tab in tabs.Elements("Tab"))
            {
                var caption = tab.Element("Caption").Value;

                if (tab.Element("SortOrder") != null)
                {
                    var sortOrder = tab.Element("SortOrder").Value;
                    docType.PropertyGroups[caption].SortOrder = int.Parse(sortOrder);
                }
            }
        }

        private static void RemoveMissingProperties(IContentType docType, XElement node)
        {
            if (!uSyncSettings.docTypeSettings.DeletePropertyValues)
            {
                LogHelper.Debug<SyncDocType>("DeletePropertyValue = false - exiting");
                return;
            }

            List<string> propertiesToRemove = new List<string>();

            foreach (var property in docType.PropertyTypes)
            {
                // is this property in our xml ?
                XElement propertyNode = node.Element("GenericProperties")
                                            .Elements("GenericProperty")
                                            .Where(x => x.Element("Alias").Value == property.Alias)
                                            .SingleOrDefault();

                if (propertyNode == null)
                {
                    // delete it from the doctype ? 
                    propertiesToRemove.Add(property.Alias);
                    LogHelper.Debug<SyncDocType>("Removing property {0} from {1}",
                        () => property.Alias, () => docType.Name);

                }
            }

            foreach (string alias in propertiesToRemove)
            {
                docType.RemovePropertyType(alias);
            }
        }

        private static void UpdateExistingProperties(IContentType docType, XElement node)
        {
            Dictionary<string, string> tabMoves = new Dictionary<string, string>();

            foreach (var property in docType.PropertyTypes)
            {
                XElement propNode = node.Element("GenericProperties")
                                        .Elements("GenericProperty")
                                        .Where(x => x.Element("Alias").Value == property.Alias)
                                        .SingleOrDefault();
                if (propNode != null)
                {
                    property.Name = propNode.Element("Name").Value;
                    property.Alias = propNode.Element("Alias").Value;
                    property.Mandatory = bool.Parse(propNode.Element("Mandatory").Value);
                    property.ValidationRegExp = propNode.Element("Validation").Value;
                    property.Description = propNode.Element("Description").Value;

                    if ( propNode.Element("SortOrder") != null )
                        property.SortOrder = int.Parse(propNode.Element("SortOrder").Value);

                    // change of type ? 
                    var defId = Guid.Parse(propNode.Element("Definition").Value);
                    var dtd = ApplicationContext.Current.Services.DataTypeService.GetDataTypeDefinitionById(defId);
                    if (dtd != null && property.DataTypeDefinitionId != dtd.Id)
                    {
                        property.DataTypeDefinitionId = dtd.Id;
                    }

                    var tabName = propNode.Element("Tab").Value;
                    if (!string.IsNullOrEmpty(tabName))
                    {
                        if (docType.PropertyGroups.Contains(tabName))
                        {
                            var propGroup = docType.PropertyGroups.First(x => x.Name == tabName);
                            if (!propGroup.PropertyTypes.Contains(property.Alias))
                            {
                                LogHelper.Info<SyncDocType>("Moving between tabs..");
                                tabMoves.Add(property.Alias, tabName);
                            }
                        }
                    }
                }
            }

            // you have to move tabs outside the loop as you are 
            // chaning the collection. 
            foreach (var move in tabMoves)
            {
                docType.MovePropertyType(move.Key, move.Value);
            }

        }

    }
}