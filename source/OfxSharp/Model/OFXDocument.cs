using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;

using ReadSharp.Ports.Sgml;

namespace OfxSharp
{
    public class OfxDocument
    {
        public static OfxDocument FromXmlElement( XmlElement ofxElement )
        {
            _ = ofxElement.AssertIsElement( "OFX" );

            XmlElement signOnMessageResponse = ofxElement.RequireSingleElementChild("SIGNONMSGSRSV1");
            XmlElement bankMessageResponse   = ofxElement.RequireSingleElementChild("BANKMSGSRSV1");

            return new OfxDocument(
                signOn    : SignOnResponse.FromXmlElement( signOnMessageResponse ),
                statements: GetStatements( bankMessageResponse )
            );
        }

        public static IEnumerable<OfxStatementResponse> GetStatements( XmlElement bankMessageResponse )
        {
            _ = bankMessageResponse.AssertIsElement( "BANKMSGSRSV1" );

            foreach( XmlNode stmTrnResponse in bankMessageResponse.SelectNodes("./STMTTRNRS") )
            {
                XmlElement stmtTrnRs = stmTrnResponse.AssertIsElement("STMTTRNRS");

                yield return OfxStatementResponse.FromSTMTTRNRS( stmtTrnRs );
            }
        }

        public OfxDocument(
            SignOnResponse                    signOn,
            IEnumerable<OfxStatementResponse> statements
        )
        {
            this.SignOn = signOn?? throw new ArgumentNullException( nameof( signOn ) );

            this.Statements.AddRange( statements );
        }

        /// <summary>SIGNONMSGSRSV1. Required. Cannot be null.</summary>
        public SignOnResponse SignOn { get; set; }

        /// <summary>BANKMSGSRSV1/STMTTRNRS</summary>
        public List<OfxStatementResponse> Statements { get; } = new List<OfxStatementResponse>();

        //

        public Boolean HasSingleStatement( out SingleStatementOfxDocument doc )
        {
            if( this.Statements.Count == 1 )
            {
                doc = new SingleStatementOfxDocument( this );
                return true;
            }
            else
            {
                doc = default;
                return false;
            }
        }
    }

    public class SingleStatementOfxDocument
    {
        public SingleStatementOfxDocument( OfxDocument ofx )
        {
            this.Ofx             = ofx ?? throw new ArgumentNullException( nameof( ofx ) );

            this.SingleStatement = ofx.Statements.Single();
        }

        public OfxDocument Ofx { get; }

        public OfxStatementResponse SingleStatement { get; }

        public DateTimeOffset             StatementStart => this.SingleStatement.TransactionsStart;
        public DateTimeOffset             StatementEnd   => this.SingleStatement.TransactionsEnd;
        public Account                    Account        => this.SingleStatement.AccountFrom;
        public IReadOnlyList<Transaction> Transactions   => this.SingleStatement.Transactions;
    }

    public static class OfxDocumentReader
    {
        private enum State
        {
            BeforeOfxHeader,
            InOfxHeader,
            StartOfOfxSgml
        }

        #region Non-async

        public static OfxDocument FromSgmlFile( String filePath )
        {
            using( FileStream fs = new FileStream( filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan ) )
            {
                return FromSgmlFile( fs );
            }
        }

        public static OfxDocument FromSgmlFile( Stream stream )
        {
            using( StreamReader rdr = new StreamReader( stream ) )
            {
                return FromSgmlFile( reader: rdr );
            }
        }

        public static OfxDocument FromSgmlFile( TextReader reader )
        {
            if( reader is null ) throw new ArgumentNullException( nameof( reader ) );

            // Read the header:
            IReadOnlyDictionary<String,String> header = ReadOfxFileHeaderUntilStartOfSgml( reader );

            XmlDocument doc = ConvertSgmlToXml( reader );

            #if DEBUG
            String xmlDocString = doc.ToXmlString();
            #endif

            return OfxDocument.FromXmlElement( doc.DocumentElement );
        }

        private static IReadOnlyDictionary<String,String> ReadOfxFileHeaderUntilStartOfSgml( TextReader reader )
        {
            Dictionary<String,String> sgmlHeaderValues = new Dictionary<String,String>();

            //

            State state = State.BeforeOfxHeader;
            String line;

            while( ( line = reader.ReadLine() ) != null )
            {
                switch( state )
                {
                case State.BeforeOfxHeader:
                    if( line.IsSet() )
                    {
                        state = State.InOfxHeader;
                    }
                    break;

                case State.InOfxHeader:

                    if( line.IsEmpty() )
                    {
                        //state = State.StartOfOfxSgml;
                        return sgmlHeaderValues;
                    }
                    else
                    {
                        String[] parts = line.Split(':');
                        String name  = parts[0];
                        String value = parts[1];
                        sgmlHeaderValues.Add( name, value );
                    }

                    break;

                case State.StartOfOfxSgml:
                    throw new InvalidOperationException( "This state should never be entered." );
                }
            }

            throw new InvalidOperationException( "Reached end of OFX file without encountering end of OFX header." );
        }

        private static XmlDocument ConvertSgmlToXml( TextReader reader )
        {
            // Convert SGML to XML:
            try
            {
                SgmlReader sgmlReader = new SgmlReader();
                sgmlReader.InputStream = reader;
                sgmlReader.DocType     = "OFX"; // <-- This causes DTD magic to happen. I don't know where it gets the DTD from though.
                sgmlReader.Dtd = new SgmlDtd(  )

                // https://stackoverflow.com/questions/1346995/how-to-create-a-xmldocument-using-xmlwriter-in-net
                XmlDocument doc = new XmlDocument(); 
                using( XmlWriter xmlWriter = doc.CreateNavigator().AppendChild() ) 
                { 
                    while( !sgmlReader.EOF )
                    {
                        xmlWriter.WriteNode( sgmlReader, defattr: true );
                    }
                }
                
                return doc;
            }
            catch( Exception ex )
            {
                throw;
            }
        }

        #endregion

        #region Async

        public static async Task<OfxDocument> FromSgmlFileAsync( Stream stream )
        {
            using( StreamReader rdr = new StreamReader( stream ) )
            {
                return await FromSgmlFileAsync( reader: rdr ).ConfigureAwait(false);
            }
        }

        public static async Task<OfxDocument> FromSgmlFileAsync( TextReader reader )
        {
            if( reader is null ) throw new ArgumentNullException( nameof( reader ) );

            // HACK: Honestly, it's easier just to buffer it all first:

            String text = await reader.ReadToEndAsync().ConfigureAwait(false);

            using( StringReader sr = new StringReader( text ) )
            {
                return FromSgmlFile( sr );
            }
        }

        #endregion
    }
}
