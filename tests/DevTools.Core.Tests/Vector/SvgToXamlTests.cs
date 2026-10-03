using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DevTools.Core.Vector;
using Xunit;

namespace DevTools.Core.Tests.Vector;

public sealed class SvgToXamlTests
{
    private static SvgConvertResult Convert(string svg, SvgConvertOptions? options = null)
    {
        var result = SvgToXaml.Convert(svg, options);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return result.Value!;
    }

    private static string Xaml(string svg, SvgConvertOptions? options = null) => Convert(svg, options).Xaml;

    private static string Wrap(string body, string? attributes = null) =>
        $"""<svg xmlns="http://www.w3.org/2000/svg" {attributes ?? "width=\"24\" height=\"24\" viewBox=\"0 0 24 24\""}>{body}</svg>""";

    /// <summary>The emitted path data, with the numbers parsed out for geometric assertions.</summary>
    private static List<double> Numbers(string pathData) =>
        [.. Regex.Matches(pathData, @"-?\d+(\.\d+)?")
            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))];

    private static string DataOf(string xaml)
    {
        var match = Regex.Match(xaml, @"Data=""([^""]*)""");
        Assert.True(match.Success, $"no Data attribute in:\n{xaml}");
        return match.Groups[1].Value;
    }

    // ---- shapes (FR-V01) -------------------------------------------------------------

    [Fact]
    public void Rect_becomes_a_closed_rectangle()
    {
        var data = DataOf(Xaml(Wrap("""<rect x="2" y="4" width="10" height="6" fill="red"/>""")));

        Assert.StartsWith("M 2,4", data, StringComparison.Ordinal);
        Assert.EndsWith("Z", data, StringComparison.Ordinal);
        Assert.Contains("12,4", data, StringComparison.Ordinal);
        Assert.Contains("12,10", data, StringComparison.Ordinal);
    }

    [Fact]
    public void Rounded_rect_uses_curves_and_clamps_the_radius_to_half_the_side()
    {
        Assert.Contains("C", DataOf(Xaml(Wrap("""<rect width="10" height="10" rx="2"/>"""))), StringComparison.Ordinal);

        // rx of 100 on a 10-wide rect must clamp to 5, not run off the shape.
        var data = DataOf(Xaml(Wrap("""<rect width="10" height="10" rx="100"/>""")));
        Assert.All(Numbers(data), n => Assert.InRange(n, -0.01, 10.01));
    }

    [Fact]
    public void A_missing_ry_mirrors_rx()
    {
        var onlyRx = DataOf(Xaml(Wrap("""<rect width="20" height="20" rx="4"/>""")));
        var both = DataOf(Xaml(Wrap("""<rect width="20" height="20" rx="4" ry="4"/>""")));
        Assert.Equal(both, onlyRx);
    }

    [Fact]
    public void Circle_and_ellipse_become_four_curves()
    {
        var circle = DataOf(Xaml(Wrap("""<circle cx="12" cy="12" r="10"/>""")));
        Assert.Equal(4, Regex.Matches(circle, @"\bC\b").Count);

        var ellipse = DataOf(Xaml(Wrap("""<ellipse cx="12" cy="12" rx="10" ry="5"/>""")));
        Assert.Equal(4, Regex.Matches(ellipse, @"\bC\b").Count);
    }

    [Fact]
    public void Zero_radius_shapes_produce_nothing()
    {
        Assert.Equal(0, Convert(Wrap("""<circle cx="1" cy="1" r="0"/>""")).ElementCount);
        Assert.Equal(0, Convert(Wrap("""<rect width="0" height="10"/>""")).ElementCount);
    }

    [Fact]
    public void Line_polyline_and_polygon()
    {
        Assert.Equal("M 0,0 L 10,10", DataOf(Xaml(Wrap("""<line x1="0" y1="0" x2="10" y2="10" stroke="black"/>"""))));

        var polyline = DataOf(Xaml(Wrap("""<polyline points="0,0 10,0 10,10" stroke="black" fill="none"/>""")));
        Assert.Equal("M 0,0 L 10,0 L 10,10", polyline);

        var polygon = DataOf(Xaml(Wrap("""<polygon points="0,0 10,0 10,10"/>""")));
        Assert.EndsWith("Z", polygon, StringComparison.Ordinal);
    }

    [Fact]
    public void Polygon_points_accept_spaces_or_commas()
    {
        Assert.Equal(
            DataOf(Xaml(Wrap("""<polygon points="0,0 10,0 10,10"/>"""))),
            DataOf(Xaml(Wrap("""<polygon points="0 0 10 0 10 10"/>"""))));
    }

    // ---- path grammar (FR-V03) --------------------------------------------------------

    [Fact]
    public void Absolute_and_relative_commands_agree()
    {
        var absolute = DataOf(Xaml(Wrap("""<path d="M 10 10 L 20 10 L 20 20 Z"/>""")));
        var relative = DataOf(Xaml(Wrap("""<path d="m 10 10 l 10 0 l 0 10 z"/>""")));
        Assert.Equal(absolute, relative);
    }

    [Fact]
    public void Implicit_command_repetition_is_honoured()
    {
        Assert.Equal("M 0,0 L 1,1 L 2,2 L 3,3", DataOf(Xaml(Wrap("""<path d="M0 0 L1 1 2 2 3 3"/>"""))));
    }

    // A repeated moveto is a lineto, per the specification.
    [Fact]
    public void A_repeated_moveto_becomes_a_lineto()
    {
        Assert.Equal("M 0,0 L 5,5 L 10,10", DataOf(Xaml(Wrap("""<path d="M0 0 5 5 10 10"/>"""))));
    }

    [Fact]
    public void Numbers_may_run_together_without_separators()
    {
        Assert.Equal("M 1,-2.5 L 0.5,0.5", DataOf(Xaml(Wrap("""<path d="M1-2.5L.5.5"/>"""))));
    }

    [Fact]
    public void Scientific_notation_is_parsed()
    {
        Assert.Equal("M 100,0.01", DataOf(Xaml(Wrap("""<path d="M1e2 1e-2"/>"""))));
    }

    [Fact]
    public void Horizontal_and_vertical_commands()
    {
        Assert.Equal("M 1,1 L 10,1 L 10,20", DataOf(Xaml(Wrap("""<path d="M1 1 H10 V20"/>"""))));
        Assert.Equal("M 1,1 L 11,1 L 11,21", DataOf(Xaml(Wrap("""<path d="M1 1 h10 v20"/>"""))));
    }

    [Fact]
    public void Smooth_cubic_reflects_the_previous_control_point()
    {
        // After C with c2 = (3,3) ending at (4,4), S's first control reflects to (5,5).
        var data = DataOf(Xaml(Wrap("""<path d="M0 0 C 1 1 3 3 4 4 S 7 7 8 8"/>""")));
        Assert.Contains("C 5,5 7,7 8,8", data, StringComparison.Ordinal);
    }

    [Fact]
    public void Smooth_cubic_without_a_previous_curve_uses_the_current_point()
    {
        Assert.Contains("C 0,0 7,7 8,8", DataOf(Xaml(Wrap("""<path d="M0 0 S 7 7 8 8"/>"""))), StringComparison.Ordinal);
    }

    [Fact]
    public void Quadratic_and_smooth_quadratic()
    {
        Assert.Contains("Q 1,1 2,0", DataOf(Xaml(Wrap("""<path d="M0 0 Q 1 1 2 0"/>"""))), StringComparison.Ordinal);

        // T reflects (1,1) about (2,0) to (3,-1).
        Assert.Contains("Q 3,-1 4,0", DataOf(Xaml(Wrap("""<path d="M0 0 Q 1 1 2 0 T 4 0"/>"""))), StringComparison.Ordinal);
    }

    [Fact]
    public void Close_returns_the_pen_to_the_start_of_the_subpath()
    {
        // After Z the current point is (0,0), so the relative lineto lands at (1,1).
        Assert.Equal("M 0,0 L 10,0 Z L 1,1", DataOf(Xaml(Wrap("""<path d="M0 0 L10 0 Z l1 1"/>"""))));
    }

    // FR-V03 — the compressed arc-flag form that real files emit.
    [Fact]
    public void Arc_flags_may_be_run_together_with_the_numbers_that_follow()
    {
        var spaced = DataOf(Xaml(Wrap("""<path d="M0 0 a1 1 0 0 1 1 1"/>""")));
        var packed = DataOf(Xaml(Wrap("""<path d="M0 0 a1 1 0 011 1"/>""")));
        Assert.Equal(spaced, packed);
    }

    [Fact]
    public void Arcs_become_cubic_curves()
    {
        var data = DataOf(Xaml(Wrap("""<path d="M0 0 A 10 10 0 0 1 20 0"/>""")));

        Assert.DoesNotContain("A", data, StringComparison.Ordinal);
        Assert.Contains("C", data, StringComparison.Ordinal);

        // The arc must still land on its stated endpoint.
        var numbers = Numbers(data);
        Assert.Equal(20, numbers[^2], 3);
        Assert.Equal(0, numbers[^1], 3);
    }

    [Fact]
    public void An_arc_with_zero_radius_degrades_to_a_line()
    {
        Assert.Equal("M 0,0 L 10,10", DataOf(Xaml(Wrap("""<path d="M0 0 A 0 0 0 0 1 10 10"/>"""))));
    }

    [Fact]
    public void An_arc_with_identical_endpoints_is_omitted()
    {
        Assert.Equal("M 5,5", DataOf(Xaml(Wrap("""<path d="M5 5 A 10 10 0 0 1 5 5"/>"""))));
    }

    // F.6.6.2 — radii too small to span the chord must be scaled up, not rejected.
    [Fact]
    public void Undersized_arc_radii_are_scaled_up_to_reach_the_endpoint()
    {
        var data = DataOf(Xaml(Wrap("""<path d="M0 0 A 1 1 0 0 1 20 0"/>""")));
        var numbers = Numbers(data);

        Assert.Equal(20, numbers[^2], 3);
        Assert.Equal(0, numbers[^1], 3);
    }

    [Fact]
    public void A_large_arc_sweep_is_split_into_several_curves()
    {
        // A 270° sweep needs at least three cubics to stay accurate.
        var data = DataOf(Xaml(Wrap("""<path d="M10 0 A 10 10 0 1 1 0 10"/>""")));
        Assert.True(Regex.Matches(data, @"\bC\b").Count >= 3);
    }

    [Fact]
    public void Truncated_path_data_warns_instead_of_throwing()
    {
        var result = Convert(Wrap("""<path d="M0 0 L 10"/>"""));
        Assert.Contains(result.Warnings, w => w.Contains("ends in the middle", StringComparison.OrdinalIgnoreCase));
    }

    // ---- transforms (FR-V04) -----------------------------------------------------------

    [Fact]
    public void Translate_scale_and_matrix_are_baked_into_the_coordinates()
    {
        Assert.Equal("M 15,25", DataOf(Xaml(Wrap("""<path d="M5 5" transform="translate(10,20)"/>"""))));
        Assert.Equal("M 10,15", DataOf(Xaml(Wrap("""<path d="M5 5" transform="scale(2,3)"/>"""))));
        Assert.Equal("M 12,23", DataOf(Xaml(Wrap("""<path d="M5 5" transform="matrix(2,0,0,3,2,8)"/>"""))));
    }

    [Fact]
    public void Rotate_about_a_point()
    {
        var data = DataOf(Xaml(Wrap("""<path d="M10 0" transform="rotate(90, 0, 0)"/>""")));
        var numbers = Numbers(data);

        Assert.Equal(0, numbers[0], 3);
        Assert.Equal(10, numbers[1], 3);
    }

    [Fact]
    public void Skew_transforms_apply()
    {
        Assert.Equal("M 15,5", DataOf(Xaml(Wrap("""<path d="M10 5" transform="skewX(45)"/>"""))));
        Assert.Equal("M 10,15", DataOf(Xaml(Wrap("""<path d="M10 5" transform="skewY(45)"/>"""))));
    }

    // SVG composes left to right: the leftmost transform is applied last.
    [Fact]
    public void Combined_transforms_compose_in_svg_order()
    {
        Assert.Equal("M 20,20", DataOf(Xaml(Wrap("""<path d="M5 5" transform="translate(10,10) scale(2)"/>"""))));
        Assert.Equal("M 30,30", DataOf(Xaml(Wrap("""<path d="M5 5" transform="scale(2) translate(10,10)"/>"""))));
    }

    [Fact]
    public void Group_transforms_nest()
    {
        var xaml = Xaml(Wrap("""<g transform="translate(10,0)"><g transform="translate(0,20)"><path d="M1 1"/></g></g>"""));
        Assert.Equal("M 11,21", DataOf(xaml));
    }

    [Fact]
    public void Stroke_width_scales_with_the_transform()
    {
        var xaml = Xaml(Wrap("""<path d="M0 0 L10 0" stroke="black" stroke-width="2" transform="scale(3)"/>"""));
        Assert.Contains("StrokeThickness=\"6\"", xaml, StringComparison.Ordinal);
    }

    // ---- viewBox (FR-V02) --------------------------------------------------------------

    [Fact]
    public void ViewBox_scales_the_geometry_to_the_declared_size()
    {
        var xaml = Xaml(Wrap("""<path d="M0 0 L100 100"/>""", """width="50" height="50" viewBox="0 0 100 100" """));

        Assert.Equal("M 0,0 L 50,50", DataOf(xaml));
        Assert.Contains("Width=\"50\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewBox_origin_is_translated_away()
    {
        Assert.Equal("M 0,0",
            DataOf(Xaml(Wrap("""<path d="M10 20"/>""", """width="100" height="100" viewBox="10 20 100 100" """))));
    }

    [Fact]
    public void Preserve_aspect_ratio_none_stretches_each_axis()
    {
        var xaml = Xaml(Wrap("""<path d="M100 100"/>""",
            """width="200" height="50" viewBox="0 0 100 100" preserveAspectRatio="none" """));

        Assert.Equal("M 200,50", DataOf(xaml));
    }

    [Fact]
    public void Preserve_aspect_ratio_meet_centres_the_content()
    {
        // 100×100 into 200×50 at "meet" scales by 0.5 and centres horizontally (+75).
        var xaml = Xaml(Wrap("""<path d="M0 0"/>""",
            """width="200" height="50" viewBox="0 0 100 100" preserveAspectRatio="xMidYMid meet" """));

        Assert.Equal("M 75,0", DataOf(xaml));
    }

    [Fact]
    public void Size_falls_back_to_the_viewbox_when_width_is_absent()
    {
        var result = Convert(Wrap("""<path d="M0 0"/>""", """viewBox="0 0 32 32" """));
        Assert.Equal(32, result.Width);
        Assert.Equal(32, result.Height);
    }

    [Fact]
    public void Css_units_on_the_size_are_converted()
    {
        // 1in is 96 CSS pixels.
        var result = Convert(Wrap("""<path d="M0 0"/>""", """width="1in" height="1in" viewBox="0 0 96 96" """));
        Assert.Equal(96, result.Width);
    }

    // ---- paint (FR-V05) -----------------------------------------------------------------

    [Theory]
    [InlineData("#f00", "#FF0000")]
    [InlineData("#ff0000", "#FF0000")]
    [InlineData("#ff000080", "#80FF0000")]
    [InlineData("rgb(255,0,0)", "#FF0000")]
    [InlineData("rgb(100%,0%,0%)", "#FF0000")]
    [InlineData("rgba(255,0,0,0.5)", "#80FF0000")]
    [InlineData("hsl(0,100%,50%)", "#FF0000")]
    [InlineData("hsl(120,100%,50%)", "#00FF00")]
    [InlineData("red", "#FF0000")]
    [InlineData("rebeccapurple", "#663399")]
    public void Colours_are_parsed_in_every_css_form(string input, string expected)
    {
        var xaml = Xaml(Wrap($"""<rect width="1" height="1" fill="{input}"/>"""));
        Assert.Contains($"Fill=\"{expected}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Fill_none_draws_nothing_and_is_not_black()
    {
        var xaml = Xaml(Wrap("""<rect width="1" height="1" fill="none" stroke="black"/>"""));
        Assert.DoesNotContain("Fill=", xaml, StringComparison.Ordinal);
        Assert.Contains("Stroke=\"#000000\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shape_with_neither_fill_nor_stroke_is_omitted()
    {
        Assert.Equal(0, Convert(Wrap("""<rect width="1" height="1" fill="none"/>""")).ElementCount);
    }

    [Fact]
    public void The_default_fill_is_black()
    {
        Assert.Contains("Fill=\"#000000\"", Xaml(Wrap("""<rect width="1" height="1"/>""")), StringComparison.Ordinal);
    }

    [Fact]
    public void Current_color_resolves_from_the_inherited_color_property()
    {
        var xaml = Xaml(Wrap("""<g color="blue"><rect width="1" height="1" fill="currentColor"/></g>"""));
        Assert.Contains("Fill=\"#0000FF\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Fill_opacity_folds_into_the_brush_alpha()
    {
        var xaml = Xaml(Wrap("""<rect width="1" height="1" fill="red" fill-opacity="0.5"/>"""));
        Assert.Contains("Fill=\"#80FF0000\"", xaml, StringComparison.Ordinal);
    }

    // Opacity is not inherited — it multiplies once per level.
    [Fact]
    public void Nested_group_opacity_multiplies()
    {
        var xaml = Xaml(Wrap("""<g opacity="0.5"><g opacity="0.5"><rect width="1" height="1" fill="red"/></g></g>"""));
        Assert.Contains("Fill=\"#40FF0000\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_style_overrides_the_presentation_attribute()
    {
        var xaml = Xaml(Wrap("""<rect width="1" height="1" fill="red" style="fill:blue"/>"""));
        Assert.Contains("Fill=\"#0000FF\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Paint_is_inherited_from_the_group()
    {
        var xaml = Xaml(Wrap("""<g fill="green"><rect width="1" height="1"/></g>"""));
        Assert.Contains("Fill=\"#008000\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Stroke_properties_are_emitted()
    {
        var xaml = Xaml(Wrap("""
            <path d="M0 0 L10 0" fill="none" stroke="black" stroke-width="3"
                  stroke-linecap="round" stroke-linejoin="bevel" stroke-miterlimit="8"/>
            """));

        Assert.Contains("StrokeThickness=\"3\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StrokeStartLineCap=\"Round\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StrokeLineJoin=\"Bevel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StrokeMiterLimit=\"8\"", xaml, StringComparison.Ordinal);
    }

    // XAML dash lengths are multiples of the stroke width; SVG's are absolute.
    [Fact]
    public void Dash_array_is_converted_to_stroke_width_multiples()
    {
        var xaml = Xaml(Wrap("""<path d="M0 0 L10 0" stroke="black" stroke-width="2" stroke-dasharray="4 2"/>"""));
        Assert.Contains("StrokeDashArray=\"2,1\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void An_odd_length_dash_pattern_is_repeated_to_become_even()
    {
        var xaml = Xaml(Wrap("""<path d="M0 0 L10 0" stroke="black" stroke-width="1" stroke-dasharray="3"/>"""));
        Assert.Contains("StrokeDashArray=\"3,3\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Fill_rule_evenodd_emits_the_f0_prefix()
    {
        Assert.StartsWith("F0", DataOf(Xaml(Wrap("""<path d="M0 0 L10 0" fill-rule="evenodd"/>"""))), StringComparison.Ordinal);
        Assert.DoesNotContain("F0", DataOf(Xaml(Wrap("""<path d="M0 0 L10 0"/>"""))), StringComparison.Ordinal);
    }

    [Fact]
    public void Display_none_and_visibility_hidden_draw_nothing()
    {
        Assert.Equal(0, Convert(Wrap("""<rect width="1" height="1" display="none"/>""")).ElementCount);
        Assert.Equal(0, Convert(Wrap("""<rect width="1" height="1" visibility="hidden"/>""")).ElementCount);
    }

    // ---- gradients (FR-V06) --------------------------------------------------------------

    [Fact]
    public void Linear_gradients_become_a_linear_gradient_brush()
    {
        var xaml = Xaml(Wrap("""
            <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
            </linearGradient></defs>
            <rect width="10" height="10" fill="url(#g)"/>
            """));

        Assert.Contains("<LinearGradientBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("StartPoint=\"0,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("EndPoint=\"1,1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Color=\"#FF0000\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Color=\"#0000FF\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Radial_gradients_become_a_radial_gradient_brush()
    {
        var xaml = Xaml(Wrap("""
            <defs><radialGradient id="g" cx="0.5" cy="0.5" r="0.5">
              <stop offset="0" stop-color="white"/><stop offset="1" stop-color="black"/>
            </radialGradient></defs>
            <circle cx="5" cy="5" r="5" fill="url(#g)"/>
            """));

        Assert.Contains("<RadialGradientBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("RadiusX=\"0.5\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void User_space_gradients_use_absolute_mapping()
    {
        var xaml = Xaml(Wrap("""
            <defs><linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="10" y2="0">
              <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
            </linearGradient></defs>
            <rect width="10" height="10" fill="url(#g)"/>
            """));

        Assert.Contains("MappingMode=\"Absolute\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Gradient_stops_are_inherited_through_href()
    {
        var xaml = Xaml(Wrap("""
            <defs>
              <linearGradient id="base"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient>
              <linearGradient id="g" href="#base" x1="0" y1="0" x2="1" y2="0"/>
            </defs>
            <rect width="10" height="10" fill="url(#g)"/>
            """));

        Assert.Contains("Color=\"#FF0000\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_opacity_folds_into_the_stop_colour()
    {
        var xaml = Xaml(Wrap("""
            <defs><linearGradient id="g"><stop offset="0" stop-color="red" stop-opacity="0.5"/>
            <stop offset="1" stop-color="blue"/></linearGradient></defs>
            <rect width="10" height="10" fill="url(#g)"/>
            """));

        Assert.Contains("Color=\"#80FF0000\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gradient_transform_is_reported_rather_than_silently_ignored()
    {
        var result = Convert(Wrap("""
            <defs><linearGradient id="g" gradientTransform="rotate(45)">
            <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient></defs>
            <rect width="10" height="10" fill="url(#g)"/>
            """));

        Assert.Contains(result.Warnings, w => w.Contains("gradientTransform", StringComparison.Ordinal));
    }

    [Fact]
    public void An_undefined_paint_server_is_reported()
    {
        var result = Convert(Wrap("""<rect width="10" height="10" fill="url(#missing)"/>"""));
        Assert.Contains(result.Warnings, w => w.Contains("#missing", StringComparison.Ordinal));
    }

    // ---- use, symbol, defs (FR-V02) ------------------------------------------------------

    [Fact]
    public void Use_instantiates_a_referenced_shape_with_an_offset()
    {
        var xaml = Xaml(Wrap("""
            <defs><rect id="r" width="4" height="4"/></defs>
            <use href="#r" x="10" y="20"/>
            """));

        Assert.Contains("M 10,20", DataOf(xaml), StringComparison.Ordinal);
    }

    [Fact]
    public void Use_works_with_the_xlink_namespace_form()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="24" height="24">
              <defs><rect id="r" width="4" height="4"/></defs>
              <use xlink:href="#r" x="5" y="5"/>
            </svg>
            """;

        Assert.Contains("M 5,5", DataOf(Xaml(svg)), StringComparison.Ordinal);
    }

    [Fact]
    public void Use_of_a_symbol_instantiates_its_children()
    {
        var xaml = Xaml(Wrap("""
            <defs><symbol id="s"><circle cx="2" cy="2" r="2"/></symbol></defs>
            <use href="#s" x="10" y="10"/>
            """));

        Assert.Contains("<Path", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dangling_use_reference_is_reported()
    {
        var result = Convert(Wrap("""<use href="#nothing"/>"""));
        Assert.Contains(result.Warnings, w => w.Contains("#nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void An_external_use_reference_is_refused()
    {
        var result = Convert(Wrap("""<use href="other.svg#icon"/>"""));
        Assert.Contains(result.Warnings, w => w.Contains("outside this file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Defs_content_is_not_drawn_on_its_own()
    {
        Assert.Equal(0, Convert(Wrap("""<defs><rect width="4" height="4"/></defs>""")).ElementCount);
    }

    // ---- unsupported features (FR-V09) ----------------------------------------------------

    [Theory]
    [InlineData("<text x=\"0\" y=\"0\">hi</text>", "text")]
    [InlineData("<filter id=\"f\"/>", "filter")]
    [InlineData("<mask id=\"m\"/>", "mask")]
    [InlineData("<pattern id=\"p\"/>", "pattern")]
    [InlineData("<image href=\"a.png\"/>", "image")]
    [InlineData("<animate attributeName=\"x\"/>", "animate")]
    [InlineData("<foreignObject/>", "foreignObject")]
    public void Unsupported_elements_are_named_in_a_warning(string body, string name)
    {
        var result = Convert(Wrap(body));
        Assert.Contains(result.Warnings, w => w.Contains($"<{name}>", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_css_style_block_is_reported_as_unsupported()
    {
        var result = Convert(Wrap("""<style>.a { fill: red }</style><rect width="1" height="1" class="a"/>"""));
        Assert.Contains(result.Warnings, w => w.Contains("<style>", StringComparison.Ordinal));
    }

    // ---- flavours and output shapes (FR-V08) -----------------------------------------------

    [Fact]
    public void WinUi_refuses_drawing_image_because_it_does_not_exist_there()
    {
        var result = SvgToXaml.Convert(Wrap("""<rect width="1" height="1"/>"""),
            new SvgConvertOptions { Flavor = XamlFlavor.WinUi, Shape = XamlOutputShape.DrawingImage });

        Assert.False(result.IsSuccess);
        Assert.Contains("WinUI", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Wpf_refuses_path_icon_because_it_does_not_exist_there()
    {
        var result = SvgToXaml.Convert(Wrap("""<rect width="1" height="1"/>"""),
            new SvgConvertOptions { Flavor = XamlFlavor.Wpf, Shape = XamlOutputShape.PathIcon });

        Assert.False(result.IsSuccess);
        Assert.Contains("WPF", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Wpf_drawing_image_emits_a_drawing_group()
    {
        var xaml = Xaml(Wrap("""<rect width="10" height="10" fill="red"/>"""),
            new SvgConvertOptions { Flavor = XamlFlavor.Wpf, Shape = XamlOutputShape.DrawingImage });

        Assert.Contains("<DrawingImage", xaml, StringComparison.Ordinal);
        Assert.Contains("<GeometryDrawing", xaml, StringComparison.Ordinal);
        Assert.Contains("Brush=\"#FF0000\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Path_icon_emits_a_single_data_string()
    {
        var xaml = Xaml(Wrap("""<rect width="10" height="10"/><circle cx="5" cy="5" r="2"/>"""),
            new SvgConvertOptions { Flavor = XamlFlavor.WinUi, Shape = XamlOutputShape.PathIcon });

        Assert.StartsWith("<PathIcon", xaml, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(xaml, "Data="));
    }

    [Fact]
    public void Merged_path_combines_every_geometry_and_warns_about_lost_colours()
    {
        var result = Convert(Wrap("""<rect width="10" height="10" fill="red"/><rect width="4" height="4" fill="blue"/>"""),
            new SvgConvertOptions { Shape = XamlOutputShape.MergedPath });

        Assert.Equal(2, Regex.Matches(DataOf(result.Xaml), @"\bM\b").Count);
        Assert.Contains(result.Warnings, w => w.Contains("different fills", StringComparison.OrdinalIgnoreCase));
    }

    // WinUI's UIElement.Clip is a RectangleGeometry and nothing else.
    [Fact]
    public void Clip_paths_are_emitted_for_wpf_and_reported_for_winui()
    {
        const string body = """
            <defs><clipPath id="c"><circle cx="5" cy="5" r="4"/></clipPath></defs>
            <rect width="10" height="10" fill="red" clip-path="url(#c)"/>
            """;

        var wpf = Convert(Wrap(body), new SvgConvertOptions { Flavor = XamlFlavor.Wpf });
        Assert.Contains("<Path.Clip>", wpf.Xaml, StringComparison.Ordinal);

        var winui = Convert(Wrap(body), new SvgConvertOptions { Flavor = XamlFlavor.WinUi });
        Assert.DoesNotContain("<Path.Clip>", winui.Xaml, StringComparison.Ordinal);
        Assert.Contains(winui.Warnings, w => w.Contains("RectangleGeometry", StringComparison.Ordinal));
    }

    [Fact]
    public void A_resource_key_makes_the_output_dictionary_ready()
    {
        var xaml = Xaml(Wrap("""<rect width="1" height="1"/>"""),
            new SvgConvertOptions { ResourceKey = "MyIcon" });

        Assert.Contains("x:Key=\"MyIcon\"", xaml, StringComparison.Ordinal);
        Assert.Contains("xmlns:x=", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Decimal_places_are_honoured()
    {
        var xaml = Xaml(Wrap("""<path d="M1.23456 2.34567"/>"""), new SvgConvertOptions { DecimalPlaces = 2 });
        Assert.Equal("M 1.23,2.35", DataOf(xaml));
    }

    // ---- failure modes ----------------------------------------------------------------------

    [Fact]
    public void Empty_input_produces_an_empty_result_not_an_error()
    {
        var result = SvgToXaml.Convert("");
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Xaml);
    }

    [Fact]
    public void Malformed_xml_fails_with_a_position()
    {
        var result = SvgToXaml.Convert("<svg><rect></svg>");
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error!.Line);
    }

    [Fact]
    public void A_non_svg_root_is_refused()
    {
        var result = SvgToXaml.Convert("<html><body/></html>");
        Assert.False(result.IsSuccess);
        Assert.Contains("not <svg>", result.ErrorMessage, StringComparison.Ordinal);
    }

    // An SVG from an untrusted source must not be able to pull in an external entity.
    [Fact]
    public void External_entities_are_refused()
    {
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE svg [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>
            <svg xmlns="http://www.w3.org/2000/svg"><rect width="1" height="1"/></svg>
            """;

        var result = SvgToXaml.Convert(xxe);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void A_self_referencing_use_is_cut_off_rather_than_recursing_forever()
    {
        var result = Convert(Wrap("""<g id="loop"><use href="#loop"/></g>"""));
        Assert.Contains(result.Warnings, w => w.Contains("references itself", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_bom_is_stripped_before_parsing()
    {
        var result = SvgToXaml.Convert("\uFEFF" + Wrap("""<rect width="1" height="1"/>"""));
        Assert.True(result.IsSuccess, result.ErrorMessage);
    }

    // ---- FR-V10: the golden corpus -----------------------------------------------------------

    /// <summary>
    /// Every output must be well-formed XML with a recognised root. In the app the same check
    /// is <c>XamlReader.Load</c>, which is stricter still — but XML validity is the part that
    /// can be asserted without a UI thread, and it catches the whole class of escaping and
    /// nesting defects.
    /// </summary>
    private static void AssertEmitsLoadableXaml(string svg, SvgConvertOptions options)
    {
        var result = Convert(svg, options);

        if (string.IsNullOrWhiteSpace(result.Xaml))
        {
            return;
        }

        var document = XDocument.Parse(result.Xaml);
        Assert.NotNull(document.Root);
        string[] expectedRoots = ["Canvas", "Path", "PathIcon", "DrawingImage"];
        Assert.Contains(document.Root!.Name.LocalName, expectedRoots);
    }

    public static TheoryData<string> Corpus()
    {
        var data = new TheoryData<string>();

        foreach (var svg in SvgCorpus.All)
        {
            data.Add(svg);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Every_corpus_file_emits_loadable_xaml_in_every_shape(string svg)
    {
        foreach (var flavor in new[] { XamlFlavor.Wpf, XamlFlavor.WinUi })
        {
            foreach (var shape in SvgConvertOptions.ShapesFor(flavor))
            {
                AssertEmitsLoadableXaml(svg, new SvgConvertOptions { Flavor = flavor, Shape = shape });
            }
        }
    }

    [Fact]
    public void The_corpus_covers_a_meaningful_range()
    {
        Assert.True(SvgCorpus.All.Count >= 25, $"the corpus has only {SvgCorpus.All.Count} files");
    }

    [Fact]
    public void Quotes_and_ampersands_in_emitted_values_are_escaped()
    {
        var xaml = Xaml(Wrap("""<rect width="1" height="1"/>"""),
            new SvgConvertOptions { ResourceKey = "a&b\"c" });

        Assert.Contains("&amp;", xaml, StringComparison.Ordinal);
        Assert.Contains("&quot;", xaml, StringComparison.Ordinal);
        XDocument.Parse(xaml);
    }
}

