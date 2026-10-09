// Ported from DevToys (https://github.com/DevToys-app/DevToys.Tools, MIT), whose cases come from
// zeroturnaround/sql-formatter (MIT). See THIRD-PARTY-NOTICES.md. The cases are kept as written,
// so the formatter is held to the layout those tools produce.

using DevTools.Core.Sql;
using Xunit;

namespace DevTools.Core.Tests.Sql;

public class SqlFormatterTests
{
    [Fact]
    public void StandardSqlFormatterTest()
    {
        var formatter = SqlDialect.StandardSql;
        SqlFormatterCases.BehavesLikeSqlFormatter(formatter);
        SqlFormatterCases.SupportsCase(formatter);
        SqlFormatterCases.SupportsCreateTable(formatter);
        SqlFormatterCases.SupportsAlterTable(formatter);
        SqlFormatterCases.SupportsStrings(formatter, "\"\"", "''");
        SqlFormatterCases.SupportsBetween(formatter);
        SqlFormatterCases.SupportsSchema(formatter);
        SqlFormatterCases.SupportsJoin(formatter);

        // formats FETCH FIRST like LIMIT
        string input = "SELECT * FETCH FIRST 2 ROWS ONLY;";
        string expectedResult =
@"SELECT
  *
FETCH FIRST
  2 ROWS ONLY;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);
    }

    [Fact]
    public void TSqlFormatterTest()
    {
        var formatter = SqlDialect.TSql;
        SqlFormatterCases.BehavesLikeSqlFormatter(formatter);
        SqlFormatterCases.SupportsCase(formatter);
        SqlFormatterCases.SupportsCreateTable(formatter);
        SqlFormatterCases.SupportsAlterTable(formatter);
        SqlFormatterCases.SupportsStrings(formatter, "\"\"", "''", "N''", "[]");
        SqlFormatterCases.SupportsBetween(formatter);
        SqlFormatterCases.SupportsSchema(formatter);
        SqlFormatterCases.SupportsOperators(formatter, "%", "&", "|", "^", "~", "!=", "!<", "!>", "+=", "-=", "*=", "/=", "%=", "|=", "&=", "^=", "::");
        SqlFormatterCases.SupportsJoin(formatter, without: new[] { "NATURAL" });

        // formats INSERT without INTO
        string input = "INSERT Customers (ID, MoneyBalance, Address, City) VALUES (12,-123.4, 'Skagen 2111','Stv');";
        string expectedResult =
@"INSERT
  Customers (ID, MoneyBalance, Address, City)
VALUES
  (12, -123.4, 'Skagen 2111', 'Stv');";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // recognizes @variables
        input = "SELECT @variable;";
        expectedResult = "SELECT\r\n  @variable;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats SELECT query with CROSS JOIN
        input = "SELECT a, b FROM t CROSS JOIN t2 on t.id = t2.id_t";
        expectedResult =
@"SELECT
  a,
  b
FROM
  t
  CROSS JOIN t2 on t.id = t2.id_t";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);
    }

    [Fact]
    public void SparkSqlFormatterTest()
    {
        var formatter = SqlDialect.SparkSql;
        SqlFormatterCases.BehavesLikeSqlFormatter(formatter);
        SqlFormatterCases.SupportsCase(formatter);
        SqlFormatterCases.SupportsCreateTable(formatter);
        SqlFormatterCases.SupportsAlterTable(formatter);
        SqlFormatterCases.SupportsStrings(formatter, "\"\"", "''", "``");
        SqlFormatterCases.SupportsBetween(formatter);
        SqlFormatterCases.SupportsSchema(formatter);
        SqlFormatterCases.SupportsOperators(formatter, "!=", "%", "|", "&", "^", "~", "!", "<=>", "%", "&&", "||", "==");
        SqlFormatterCases.SupportsJoin(
            formatter,
            additionally: new[]
            {
                "ANTI JOIN",
                "SEMI JOIN",
                "LEFT ANTI JOIN",
                "LEFT SEMI JOIN",
                "RIGHT OUTER JOIN",
                "RIGHT SEMI JOIN",
                "NATURAL ANTI JOIN",
                "NATURAL FULL OUTER JOIN",
                "NATURAL INNER JOIN",
                "NATURAL LEFT ANTI JOIN",
                "NATURAL LEFT OUTER JOIN",
                "NATURAL LEFT SEMI JOIN",
                "NATURAL OUTER JOIN",
                "NATURAL RIGHT OUTER JOIN",
                "NATURAL RIGHT SEMI JOIN",
                "NATURAL SEMI JOIN"
            });

        // formats WINDOW specification as top level
        string input = "SELECT *, LAG(value) OVER wnd AS next_value FROM tbl WINDOW wnd as (PARTITION BY id ORDER BY time);";
        string expectedResult =
@"SELECT
  *,
  LAG(value) OVER wnd AS next_value
FROM
  tbl
WINDOW
  wnd as (
    PARTITION BY
      id
    ORDER BY
      time
  );";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats window function and end as inline
        input = "SELECT window(time, \"1 hour\").start AS window_start, window(time, \"1 hour\").end AS window_end FROM tbl;";
        expectedResult = "SELECT\r\n  window(time, \"1 hour\").start AS window_start,\r\n  window(time, \"1 hour\").end AS window_end\r\nFROM\r\n  tbl;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);
    }

    [Fact]
    public void RedshiftFormatterTest()
    {
        var formatter = SqlDialect.Redshift;
        SqlFormatterCases.BehavesLikeSqlFormatter(formatter);
        SqlFormatterCases.SupportsCreateTable(formatter);
        SqlFormatterCases.SupportsAlterTable(formatter);
        SqlFormatterCases.SupportsAlterTableModify(formatter);
        SqlFormatterCases.SupportsStrings(formatter, "\"\"", "''", "``");
        SqlFormatterCases.SupportsSchema(formatter);
        SqlFormatterCases.SupportsOperators(formatter, "%", "^", "|/", "||/", "<<", ">>", "&", "|", "~", "!", "!=", "||");
        SqlFormatterCases.SupportsJoin(formatter);

        // formats LIMIT
        string input = "SELECT col1 FROM tbl ORDER BY col2 DESC LIMIT 10;";
        string expectedResult =
@"SELECT
  col1
FROM
  tbl
ORDER BY
  col2 DESC
LIMIT
  10;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats only -- as a line comment
        input =
@"SELECT col FROM
-- This is a comment
MyTable;";
        expectedResult =
@"SELECT
  col
FROM
  -- This is a comment
  MyTable;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // recognizes @ as part of identifiers
        input = @"SELECT @col1 FROM tbl";
        expectedResult =
@"SELECT
  @col1
FROM
  tbl";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats DISTKEY and SORTKEY after CREATE TABLE
        input = @"CREATE TABLE items (a INT PRIMARY KEY, b TEXT, c INT NOT NULL, d INT NOT NULL) DISTKEY(created_at) SORTKEY(created_at);";
        expectedResult =
@"CREATE TABLE items (a INT PRIMARY KEY, b TEXT, c INT NOT NULL, d INT NOT NULL)
DISTKEY
(created_at)
SORTKEY
(created_at);";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats COPY
        input =
@"COPY schema.table
FROM 's3://bucket/file.csv'
IAM_ROLE 'arn:aws:iam::123456789:role/rolename'
FORMAT AS CSV DELIMITER ',' QUOTE ''
REGION AS 'us-east-1'";
        expectedResult =
@"COPY
  schema.table
FROM
  's3://bucket/file.csv'
IAM_ROLE
  'arn:aws:iam::123456789:role/rolename'
FORMAT
  AS CSV
DELIMITER
  ',' QUOTE ''
REGION
  AS 'us-east-1'";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);
    }

    [Fact]
    public void PostgreSqlFormatterTest()
    {
        var formatter = SqlDialect.PostgreSql;
        SqlFormatterCases.BehavesLikeSqlFormatter(formatter);
        SqlFormatterCases.SupportsCase(formatter);
        SqlFormatterCases.SupportsCreateTable(formatter);
        SqlFormatterCases.SupportsAlterTable(formatter);
        SqlFormatterCases.SupportsStrings(formatter, "\"\"", "''", "U&\"\"", "U&''", "$$");
        SqlFormatterCases.SupportsBetween(formatter);
        SqlFormatterCases.SupportsSchema(formatter);
        SqlFormatterCases.SupportsOperators(
            formatter,
            "%",
            "^",
            "!",
            "!!",
            "!=",
            "&",
            "|",
            "~",
            "#",
            "<<",
            ">>",
            "||/",
            "|/",
            "::",
            "->>",
            "->",
            "~~*",
            "~~",
            "!~~*",
            "!~~",
            "~*",
            "!~*",
            "!~");
        SqlFormatterCases.SupportsJoin(formatter);

        // supports $n placeholders
        string input = "SELECT $1, $2 FROM tbl";
        string expectedResult =
@"SELECT
  $1,
  $2
FROM
  tbl";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // supports :name placeholders
        input = "foo = :bar";
        expectedResult = input;
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);
    }

    [Fact]
    public void PlSqlFormatterTest()
    {
        var formatter = SqlDialect.PlSql;
        SqlFormatterCases.BehavesLikeSqlFormatter(formatter);
        SqlFormatterCases.SupportsCase(formatter);
        SqlFormatterCases.SupportsCreateTable(formatter);
        SqlFormatterCases.SupportsAlterTable(formatter);
        SqlFormatterCases.SupportsAlterTableModify(formatter);
        SqlFormatterCases.SupportsStrings(formatter, "\"\"", "''", "``");
        SqlFormatterCases.SupportsBetween(formatter);
        SqlFormatterCases.SupportsSchema(formatter);
        SqlFormatterCases.SupportsOperators(formatter, "||", "**", "!=", ":=");
        SqlFormatterCases.SupportsJoin(formatter);

        // formats FETCH FIRST like LIMIT
        string input = "SELECT col1 FROM tbl ORDER BY col2 DESC FETCH FIRST 20 ROWS ONLY;";
        string expectedResult =
@"SELECT
  col1
FROM
  tbl
ORDER BY
  col2 DESC
FETCH FIRST
  20 ROWS ONLY;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats only -- as a line comment
        input = "SELECT col FROM\r\n-- This is a comment\r\nMyTable;\r\n";
        expectedResult =
@"SELECT
  col
FROM
  -- This is a comment
  MyTable;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // recognizes _, $, #, . and @ as part of identifiers
        input = "SELECT my_col$1#, col.2@ FROM tbl\r\n";
        expectedResult =
@"SELECT
  my_col$1#,
  col.2@
FROM
  tbl";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats INSERT without INTO
        input = "INSERT Customers (ID, MoneyBalance, Address, City) VALUES (12,-123.4, 'Skagen 2111','Stv');";
        expectedResult =
@"INSERT
  Customers (ID, MoneyBalance, Address, City)
VALUES
  (12, -123.4, 'Skagen 2111', 'Stv');";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // recognizes ?[0-9]* placeholders
        input = "SELECT ?1, ?25, ?;";
        expectedResult =
@"SELECT
  ?1,
  ?25,
  ?;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats SELECT query with CROSS APPLY
        input = "SELECT a, b FROM t CROSS APPLY fn(t.id)";
        expectedResult =
@"SELECT
  a,
  b
FROM
  t
  CROSS APPLY fn(t.id)";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats simple SELECT
        input = "SELECT N, M FROM t";
        expectedResult =
@"SELECT
  N,
  M
FROM
  t";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats simple SELECT with national characters
        input = "SELECT N'value'";
        expectedResult =
@"SELECT
  N'value'";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats SELECT query with OUTER APPLY
        input = "SELECT a, b FROM t OUTER APPLY fn(t.id)";
        expectedResult =
@"SELECT
  a,
  b
FROM
  t
  OUTER APPLY fn(t.id)";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats Oracle recursive sub queries
        input =
@"WITH t1(id, parent_id) AS (
  -- Anchor member.
  SELECT
    id,
    parent_id
  FROM
    tab1
  WHERE
    parent_id IS NULL
  MINUS
    -- Recursive member.
  SELECT
    t2.id,
    t2.parent_id
  FROM
    tab1 t2,
    t1
  WHERE
    t2.parent_id = t1.id
) SEARCH BREADTH FIRST BY id SET order1,
another AS (SELECT * FROM dual)
SELECT id, parent_id FROM t1 ORDER BY order1;";
        expectedResult =
@"WITH t1(id, parent_id) AS (
  -- Anchor member.
  SELECT
    id,
    parent_id
  FROM
    tab1
  WHERE
    parent_id IS NULL
  MINUS
  -- Recursive member.
  SELECT
    t2.id,
    t2.parent_id
  FROM
    tab1 t2,
    t1
  WHERE
    t2.parent_id = t1.id
) SEARCH BREADTH FIRST BY id SET order1,
another AS (
  SELECT
    *
  FROM
    dual
)
SELECT
  id,
  parent_id
FROM
  t1
ORDER BY
  order1;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats Oracle recursive sub queries regardless of capitalization
        input =
@"WITH t1(id, parent_id) AS (
  -- Anchor member.
  SELECT
    id,
    parent_id
  FROM
    tab1
  WHERE
    parent_id IS NULL
  MINUS
    -- Recursive member.
  SELECT
    t2.id,
    t2.parent_id
  FROM
    tab1 t2,
    t1
  WHERE
    t2.parent_id = t1.id
) SEARCH BREADTH FIRST by id set order1,
another AS (SELECT * FROM dual)
SELECT id, parent_id FROM t1 ORDER BY order1;";
        expectedResult =
@"WITH t1(id, parent_id) AS (
  -- Anchor member.
  SELECT
    id,
    parent_id
  FROM
    tab1
  WHERE
    parent_id IS NULL
  MINUS
  -- Recursive member.
  SELECT
    t2.id,
    t2.parent_id
  FROM
    tab1 t2,
    t1
  WHERE
    t2.parent_id = t1.id
) SEARCH BREADTH FIRST by id set order1,
another AS (
  SELECT
    *
  FROM
    dual
)
SELECT
  id,
  parent_id
FROM
  t1
ORDER BY
  order1;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);
    }

    [Fact]
    public void N1qlFormatterTest()
    {
        var formatter = SqlDialect.N1ql;
        SqlFormatterCases.BehavesLikeSqlFormatter(formatter);
        SqlFormatterCases.SupportsStrings(formatter, "\"\"", "''", "``");
        SqlFormatterCases.SupportsBetween(formatter);
        SqlFormatterCases.SupportsSchema(formatter);
        SqlFormatterCases.SupportsOperators(formatter, "%", "==", "!=");
        SqlFormatterCases.SupportsJoin(formatter, without: new[] { "FULL", "CROSS", "NATURAL" });

        // formats SELECT query with element selection expression
        string input = "SELECT order_lines[0].productId FROM orders;";
        string expectedResult =
@"SELECT
  order_lines[0].productId
FROM
  orders;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats SELECT query with primary key querying
        input = "SELECT fname, email FROM tutorial USE KEYS ['dave', 'ian'];";
        expectedResult =
@"SELECT
  fname,
  email
FROM
  tutorial
USE KEYS
  ['dave', 'ian'];";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats INSERT with {} object literal
        input = "INSERT INTO heroes (KEY, VALUE) VALUES ('123', {'id':1,'type':'Tarzan'});";
        expectedResult =
@"INSERT INTO
  heroes (KEY, VALUE)
VALUES
  ('123', {'id': 1, 'type': 'Tarzan'});";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats INSERT with large object and array literals
        input = "INSERT INTO heroes (KEY, VALUE) VALUES ('123', {'id': 1, 'type': 'Tarzan', 'array': [123456789, 123456789, 123456789, 123456789, 123456789], 'hello': 'world'});";
        expectedResult =
@"INSERT INTO
  heroes (KEY, VALUE)
VALUES
  (
    '123',
    {
      'id': 1,
      'type': 'Tarzan',
      'array': [
        123456789,
        123456789,
        123456789,
        123456789,
        123456789
      ],
      'hello': 'world'
    }
  );";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats SELECT query with UNNEST top level reserver word
        input = "SELECT * FROM tutorial UNNEST tutorial.children c;";
        expectedResult =
@"SELECT
  *
FROM
  tutorial
UNNEST
  tutorial.children c;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats SELECT query with NEST and USE KEYS
        input =
@"SELECT * FROM usr
USE KEYS 'Elinor_33313792' NEST orders_with_users orders
ON KEYS ARRAY s.order_id FOR s IN usr.shipped_order_history END;";
        expectedResult =
@"SELECT
  *
FROM
  usr
USE KEYS
  'Elinor_33313792'
NEST
  orders_with_users orders ON KEYS ARRAY s.order_id FOR s IN usr.shipped_order_history END;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats explained DELETE query with USE KEYS and RETURNING
        input = "EXPLAIN DELETE FROM tutorial t USE KEYS 'baldwin' RETURNING t";
        expectedResult =
@"EXPLAIN DELETE FROM
  tutorial t
USE KEYS
  'baldwin' RETURNING t";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats UPDATE query with USE KEYS and RETURNING
        input = "UPDATE tutorial USE KEYS 'baldwin' SET type = 'actor' RETURNING tutorial.type";
        expectedResult =
@"UPDATE
  tutorial
USE KEYS
  'baldwin'
SET
  type = 'actor' RETURNING tutorial.type";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // recognizes $variables
        input = "SELECT $variable;";
        expectedResult = "SELECT\r\n  $variable;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);
    }

    [Fact]
    public void MySqlFormatterTest()
    {
        var formatter = SqlDialect.MySql;
        SqlFormatterCases.BehavesLikeMariaDbFormatter(formatter);
        SqlFormatterCases.SupportsOperators(formatter, "->", "->>");
    }

    [Fact]
    public void MariaDbFormatterTest()
    {
        var formatter = SqlDialect.MariaDb;
        SqlFormatterCases.BehavesLikeMariaDbFormatter(formatter);
    }

    [Fact]
    public void Db2FormatterTest()
    {
        var formatter = SqlDialect.Db2;
        SqlFormatterCases.BehavesLikeSqlFormatter(formatter);
        SqlFormatterCases.SupportsCreateTable(formatter);
        SqlFormatterCases.SupportsAlterTable(formatter);
        SqlFormatterCases.SupportsStrings(formatter, "\"\"", "''", "``");
        SqlFormatterCases.SupportsBetween(formatter);
        SqlFormatterCases.SupportsSchema(formatter);
        SqlFormatterCases.SupportsOperators(formatter, "%", "**", "!=", "!>", "!>", "||");

        // formats FETCH FIRST like LIMIT
        string input = "SELECT col1 FROM tbl ORDER BY col2 DESC FETCH FIRST 20 ROWS ONLY;";
        string expectedResult =
@"SELECT
  col1
FROM
  tbl
ORDER BY
  col2 DESC
FETCH FIRST
  20 ROWS ONLY;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // formats only -- as a line comment
        input =
@"SELECT col FROM
-- This is a comment
MyTable;";
        expectedResult =
@"SELECT
  col
FROM
  -- This is a comment
  MyTable;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // recognizes @ and # as part of identifiers
        input = "SELECT col#1, @col2 FROM tbl";
        expectedResult =
@"SELECT
  col#1,
  @col2
FROM
  tbl";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);

        // recognizes :variables
        input = "SELECT :variable;";
        expectedResult =
@"SELECT
  :variable;";
        SqlFormatterCases.AssertFormat(formatter, input, expectedResult);
    }
}
