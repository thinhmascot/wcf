// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace MS.Internal.Xml.XPath
{
    using System;
    using Microsoft.Xml;
    using Microsoft.Xml.XPath;
    using System.Diagnostics;
    using System.Globalization;
    using System.Collections;
    using Microsoft.Xml.Xsl;

    internal class CompiledXpathExpr : XPathExpression
    {
        private Query _query;
        private string _expr;
        private bool _needContext;

        internal CompiledXpathExpr(Query query, string expression, bool needContext)
        {
            _query = query;
            _expr = expression;
            _needContext = needContext;
        }

        internal Query QueryTree
        {
            get
            {
                if (_needContext)
                {
                    throw XPathException.Create(ResXml.Xp_NoContext);
                }
                return _query;
            }
        }

        public override string Expression
        {
            get { return _expr; }
        }

        public virtual void CheckErrors()
        {
            Debug.Assert(_query != null, "In case of error in XPath we create ErrorXPathExpression");
        }

        public override void AddSort(object expr, IComparer comparer)
        {
            // sort makes sense only when we are dealing with a query that
            // returns a nodeset.
            Query evalExpr;
            if (expr is string)
            {
                evalExpr = new QueryBuilder().Build((string)expr, out _needContext); // this will throw if expr is invalid
            }
            else if (expr is CompiledXpathExpr)
            {
                evalExpr = ((CompiledXpathExpr)expr).QueryTree;
            }
            else
            {
                throw XPathException.Create(ResXml.Xp_BadQueryObject);
            }
            SortQuery sortQuery = _query as SortQuery;
            if (sortQuery == null)
            {
                _query = sortQuery = new SortQuery(_query);
            }
            sortQuery.AddSort(evalExpr, comparer);
        }

        public override void AddSort(object expr, XmlSortOrder order, XmlCaseOrder caseOrder, string lang, XmlDataType dataType)
        {
            AddSort(expr, new XPathComparerHelper(order, caseOrder, lang, dataType));
        }

        public override XPathExpression Clone()
        {
            return new CompiledXpathExpr(Query.Clone(_query), _expr, _needContext);
        }

        public override void SetContext(XmlNamespaceManager nsManager)
        {
            SetContext((IXmlNamespaceResolver)nsManager);
        }

        public override void SetContext(IXmlNamespaceResolver nsResolver)
        {
            XsltContext xsltContext = nsResolver as XsltContext;
            if (xsltContext == null)
            {
                if (nsResolver == null)
                {
                    nsResolver = new XmlNamespaceManager(new NameTable());
                }
                xsltContext = new UndefinedXsltContext(nsResolver);
            }
            _query.SetXsltContext(xsltContext);

            _needContext = false;
        }

        public override XPathResultType ReturnType { get { return _query.StaticType; } }

        private class UndefinedXsltContext : XsltContext
        {
            private IXmlNamespaceResolver _nsResolver;

            public UndefinedXsltContext(IXmlNamespaceResolver nsResolver) : base(/*dummy*/false)
            {
                _nsResolver = nsResolver;
            }
            //----- Namespace support -----
            public override string DefaultNamespace
            {
                get { return string.Empty; }
            }
            public override string LookupNamespace(string prefix)
            {
                Debug.Assert(prefix != null);
                if (prefix.Length == 0)
                {
                    return string.Empty;
                }
                string ns = _nsResolver.LookupNamespace(prefix);
                if (ns == null)
                {
                    throw XPathException.Create(ResXml.XmlUndefinedAlias, prefix);
                }
                return ns;
            }
            //----- XsltContext support -----
            public override IXsltContextVariable ResolveVariable(string prefix, string name)
            {
                throw XPathException.Create(ResXml.Xp_UndefinedXsltContext);
            }
            public override IXsltContextFunction ResolveFunction(string prefix, string name, XPathResultType[] ArgTypes)
            {
                throw XPathException.Create(ResXml.Xp_UndefinedXsltContext);
            }
            public override bool Whitespace { get { return false; } }
            public override bool PreserveWhitespace(XPathNavigator node) { return false; }
            public override int CompareDocument(string baseUri, string nextbaseUri)
            {
                return string.CompareOrdinal(baseUri, nextbaseUri);
            }
        }
    }

    internal sealed class XPathComparerHelper : IComparer
    {
        private XmlSortOrder _order;
        private XmlCaseOrder _caseOrder;
        private CultureInfo _cinfo;
        private XmlDataType _dataType;

        public XPathComparerHelper(XmlSortOrder order, XmlCaseOrder caseOrder, string lang, XmlDataType dataType)
        {
            if (lang == null)
            {
                _cinfo = CultureInfo.CurrentCulture; // System.Threading.Thread.CurrentThread.CurrentCulture;
            }
            else
            {
                try
                {
                    _cinfo = new CultureInfo(lang);
                }
                catch (System.ArgumentException)
                {
                    throw;  // Throwing an XsltException would be a breaking change
                }
            }

            if (order == XmlSortOrder.Descending)
            {
                if (caseOrder == XmlCaseOrder.LowerFirst)
                {
                    caseOrder = XmlCaseOrder.UpperFirst;
                }
                else if (caseOrder == XmlCaseOrder.UpperFirst)
                {
                    caseOrder = XmlCaseOrder.LowerFirst;
                }
            }

            _order = order;
            _caseOrder = caseOrder;
            _dataType = dataType;
        }

        public int Compare(object x, object y)
        {
            switch (_dataType)
            {
                case XmlDataType.Text:
                    string s1 = Convert.ToString(x, _cinfo);
                    string s2 = Convert.ToString(y, _cinfo);
                    int result = string.Compare(s1, s2, /*ignoreCase:*/ _caseOrder != XmlCaseOrder.None); //, this.cinfo);

                    if (result != 0 || _caseOrder == XmlCaseOrder.None)
                        return (_order == XmlSortOrder.Ascending) ? result : -result;

                    // If we came this far, it means that strings s1 and s2 are
                    // equal to each other when case is ignored. Now it's time to check
                    // and see if they differ in case only and take into account the user
                    // requested case order for sorting purposes.
                    result = string.Compare(s1, s2, /*ignoreCase:*/ false); //, this.cinfo);
                    return (_caseOrder == XmlCaseOrder.LowerFirst) ? result : -result;

                case XmlDataType.Number:
                    double r1 = XmlConvert.ToXPathDouble(x);
                    double r2 = XmlConvert.ToXPathDouble(y);
                    result = r1.CompareTo(r2);
                    return (_order == XmlSortOrder.Ascending) ? result : -result;

                default:
                    // dataType doesn't support any other value
                    throw new InvalidOperationException(ResXml.Xml_InvalidOperation);
            }
        } // Compare ()
    } // class XPathComparerHelper
}
