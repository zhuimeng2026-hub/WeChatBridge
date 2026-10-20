package postgres

// Real-database tests for the store.
//
// The rest of this package runs on pgxmock, which asserts that a query string
// looks right and never executes it. That covers the shape of a query — the
// column list, the parameter order, a typo in a WHERE clause — and covers
// nothing about its behaviour. Everything that decides what a query RETURNS or
// which of two concurrent writers wins lives in the database: partial unique
// indexes, conditional UPDATE guards and how many rows they actually touched,
// the implicit ordering of a result set.
//
// That is not a hypothetical gap. §11.7 M-5 shipped a query whose whole point
// was "which row does the poller wait on", and the entire package stayed green
// when that query was reverted to the version containing the bug, because the
// test for it was a fake that implemented whatever rule the fake was written
// to. These tests close that gap for the queries whose meaning is semantic.
//
// Set WCB_TEST_DATABASE_URL to run them. Without it every test here skips, so
// CI — which has no PostgreSQL — is unaffected. That was deliberate: A-⑧ wants
// real coverage, and a test that only runs on one person's laptop is a test
// that quietly stops being run.
//
// Each test gets its own schema built from the real migrations, so a schema
// change cannot quietly invalidate a fixture the way a hand-copied CREATE TABLE
// would. Point WCB_TEST_DATABASE_URL at a scratch database you are willing to
// have schemas created and dropped in; do not point it at production.

import (
	"context"
	"fmt"
	"net/url"
	"os"
	"sort"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/freestylefly/WeChatBridge/server/migrations"
)

// EnvRealDatabaseURL is the opt-in switch. Named rather than inlined so the
// skip message and any documentation cannot drift apart.
const EnvRealDatabaseURL = "WCB_TEST_DATABASE_URL"

// realDBCounter names schemas uniquely so parallel tests never collide.
var realDBCounter atomic.Int64

// realDB is one test's isolated schema: a Store whose connections all have this
// schema as their search_path, built by applying the real migrations.
type realDB struct {
	*Store
	schema string
	dsn    string
}

// newRealDB builds an isolated, migrated schema and returns a Store bound to
// it. The schema is dropped when the test ends, pass or fail.
//
// Migrations are applied for real rather than from a copy of the DDL. A
// hand-copied fixture is a second description of the schema, and the day the
// migration and the copy disagree, the copy is the one that keeps passing.
func newRealDB(t *testing.T) *realDB {
	t.Helper()
	baseDSN := strings.TrimSpace(os.Getenv(EnvRealDatabaseURL))
	if baseDSN == "" {
		t.Skipf("%s is not set; skipping the real-database test", EnvRealDatabaseURL)
	}

	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()

	schema := fmt.Sprintf("wcb_test_%d_%d", os.Getpid(), realDBCounter.Add(1))

	// Connect once without a search_path to create the schema, then again with
	// it. A schema per test rather than one shared schema with a truncated
	// table between tests: t.Parallel() is real here, and shared state across
	// parallel tests is how a suite becomes order-dependent and untrustworthy.
	bootstrap, err := New(ctx, baseDSN)
	if err != nil {
		t.Skipf("cannot reach the test database: %v", err)
	}
	if _, err := bootstrap.db.Exec(ctx, "CREATE SCHEMA "+quoteIdent(schema)); err != nil {
		_ = bootstrap.Close()
		t.Skipf("cannot create a schema (does this role own the database?): %v", err)
	}

	dsn := withSearchPath(baseDSN, schema)
	store, err := New(ctx, dsn)
	if err != nil {
		t.Fatalf("connect with search_path: %v", err)
	}
	t.Cleanup(func() {
		_ = store.Close()
		cleanup, cctx := context.WithTimeout(context.Background(), 30*time.Second)
		defer cctxCancel(cctx)
		// Best effort, on a fresh connection: the store is already closed.
		if b, berr := New(cleanup, baseDSN); berr == nil {
			_, _ = b.db.Exec(cleanup, "DROP SCHEMA IF EXISTS "+quoteIdent(schema)+" CASCADE")
			_ = b.Close()
		}
	})

	applyMigrations(t, ctx, store)

	return &realDB{Store: store, schema: schema, dsn: dsn}
}

// cctxCancel exists only so the cleanup closure reads as one idea rather than
// three; context.CancelFunc is what WithTimeout already returned.
func cctxCancel(cancel context.CancelFunc) { cancel() }

// applyMigrations runs every .up.sql in version order against the freshly
// created schema, which is exactly what the service does at boot.
func applyMigrations(t *testing.T, ctx context.Context, s *Store) {
	t.Helper()
	entries, err := migrations.FS.ReadDir(".")
	if err != nil {
		t.Fatalf("read embedded migrations: %v", err)
	}
	var names []string
	for _, e := range entries {
		if !e.IsDir() && strings.HasSuffix(e.Name(), ".up.sql") {
			names = append(names, e.Name())
		}
	}
	sort.Strings(names)
	if len(names) == 0 {
		t.Fatal("no migrations found — the harness would be testing an empty schema")
	}
	for _, name := range names {
		raw, err := migrations.FS.ReadFile(name)
		if err != nil {
			t.Fatalf("read %s: %v", name, err)
		}
		if _, err := s.db.Exec(ctx, string(raw)); err != nil {
			t.Fatalf("apply %s: %v", name, err)
		}
	}
}

// withSearchPath adds a runtime parameter to the DSN, keeping whatever query
// string it already had.
func withSearchPath(dsn, schema string) string {
	u, err := url.Parse(dsn)
	if err != nil {
		// A DSN this package cannot parse is one it also cannot have been
		// constructed with; fall back to the key=value form rather than
		// silently testing against the wrong schema.
		sep := "?"
		if strings.Contains(dsn, "?") {
			sep = "&"
		}
		return dsn + sep + "search_path=" + schema
	}
	q := u.Query()
	q.Set("search_path", schema)
	u.RawQuery = q.Encode()
	return u.String()
}

// quoteIdent makes a schema name safe to interpolate. Schema names here are
// generated, never user input, but an identifier spliced into SQL without
// quoting is a habit worth not having.
func quoteIdent(name string) string {
	return `"` + strings.ReplaceAll(name, `"`, `""`) + `"`
}

// exec is a small convenience for assertions that need to look at the database
// directly rather than through the Store — the shape of a row, or how many rows
// a constraint really left behind.
func (r *realDB) exec(t *testing.T, sql string, args ...any) {
	t.Helper()
	if _, err := r.db.Exec(context.Background(), sql, args...); err != nil {
		t.Fatalf("exec %q: %v", sql, err)
	}
}

// queryInt runs a query expected to yield a single integer.
func (r *realDB) queryInt(t *testing.T, sql string, args ...any) int {
	t.Helper()
	var n int
	if err := r.db.QueryRow(context.Background(), sql, args...).Scan(&n); err != nil {
		t.Fatalf("query %q: %v", sql, err)
	}
	return n
}

// queryStrings runs a query expected to yield a single text column.
func (r *realDB) queryStrings(t *testing.T, sql string, args ...any) []string {
	t.Helper()
	rows, err := r.db.Query(context.Background(), sql, args...)
	if err != nil {
		t.Fatalf("query %q: %v", sql, err)
	}
	defer rows.Close()
	var out []string
	for rows.Next() {
		var s string
		if err := rows.Scan(&s); err != nil {
			t.Fatalf("scan %q: %v", sql, err)
		}
		out = append(out, s)
	}
	if err := rows.Err(); err != nil {
		t.Fatalf("iterate %q: %v", sql, err)
	}
	return out
}
