using Coder.Core.Svg;

namespace Coder.Core.Tests;

public sealed class SvgDocumentTests
{
    private static SvgDocument Parse(string body, string rootAttributes = "width=\"100\" height=\"100\"") =>
        SvgDocument.Parse($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" {rootAttributes}>{body}</svg>");

    private static SvgShape Shape(string body, string rootAttributes = "width=\"100\" height=\"100\"") =>
        Assert.IsType<SvgShape>(Assert.Single(Parse(body, rootAttributes).Items));

    private static SvgColor Rgb(byte r, byte g, byte b, byte a = 255) => new(r, g, b, a);

    private static void Near(double expected, double actual, double tolerance = 1e-6) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}");

    private static void Point(SvgMatrix m, double x, double y, double ex, double ey)
    {
        var (px, py) = m.Apply(x, y);
        Near(ex, px);
        Near(ey, py);
    }

    // ---- Root size and viewBox ----

    [Fact]
    public void Root_size_comes_from_width_and_height_with_units()
    {
        var document = Parse("", "width=\"72pt\" height=\"2em\"");
        Near(96, document.Width);
        Near(32, document.Height);
        Assert.Null(document.ViewBox);
    }

    [Theory]
    [InlineData("width=\"10mm\" height=\"1in\"", 37.795275, 96)]
    [InlineData("width=\"2cm\" height=\"1pc\"", 75.590551, 16)]
    [InlineData("width=\"50px\" height=\"25\"", 50, 25)]
    public void Absolute_units_convert_at_96_dpi(string attributes, double width, double height)
    {
        var document = Parse("", attributes);
        Near(width, document.Width, 1e-4);
        Near(height, document.Height, 1e-4);
    }

    [Fact]
    public void Missing_size_comes_from_the_view_box()
    {
        var document = Parse("", "viewBox=\"0 0 24 12\"");
        Assert.Equal(new SvgViewBox(0, 0, 24, 12), document.ViewBox);
        Near(24, document.Width);
        Near(12, document.Height);
    }

    [Fact]
    public void One_missing_dimension_keeps_the_view_box_aspect()
    {
        var document = Parse("", "width=\"48\" viewBox=\"0 0 24 12\"");
        Near(48, document.Width);
        Near(24, document.Height);
    }

    [Fact]
    public void Percent_size_falls_back_to_the_view_box_and_no_size_defaults_to_300_by_150()
    {
        var percent = Parse("", "width=\"100%\" height=\"100%\" viewBox=\"0 0 10 20\"");
        Near(10, percent.Width);
        Near(20, percent.Height);

        var none = Parse("", "");
        Near(300, none.Width);
        Near(150, none.Height);
    }

    [Fact]
    public void Absurd_sizes_are_clamped_and_bad_view_boxes_ignored()
    {
        var document = Parse("", "width=\"1e12\" height=\"5\" viewBox=\"0 0 -1 10\"");
        Near(SvgDocument.MaxSize, document.Width);
        Near(5, document.Height);
        Assert.Null(document.ViewBox);
    }

    [Fact]
    public void View_box_maps_into_the_viewport_centered_and_uniform()
    {
        var rect = Shape("<rect width=\"10\" height=\"10\"/>", "width=\"200\" height=\"100\" viewBox=\"0 0 10 10\"");
        // Scale 10 (meet), centered horizontally: x offset (200 - 100) / 2 = 50.
        Point(rect.Transform, 0, 0, 50, 0);
        Point(rect.Transform, 10, 10, 150, 100);
    }

    [Fact]
    public void Preserve_aspect_ratio_none_and_slice()
    {
        var none = Shape("<rect width=\"10\" height=\"10\"/>", "width=\"200\" height=\"100\" viewBox=\"0 0 10 10\" preserveAspectRatio=\"none\"");
        Point(none.Transform, 10, 10, 200, 100);

        var slice = Shape("<rect width=\"10\" height=\"10\"/>", "width=\"200\" height=\"100\" viewBox=\"0 0 10 10\" preserveAspectRatio=\"xMinYMin slice\"");
        Point(slice.Transform, 10, 10, 200, 200);
    }

    [Fact]
    public void View_box_origin_is_translated_away()
    {
        var rect = Shape("<rect x=\"5\" y=\"5\" width=\"1\" height=\"1\"/>", "width=\"10\" height=\"10\" viewBox=\"5 5 10 10\"");
        Point(rect.Transform, 5, 5, 0, 0);
    }

    // ---- Shapes to path data ----

    [Fact]
    public void Rect_becomes_a_closed_path()
    {
        Assert.Equal("M 10 20 H 40 V 60 H 10 Z", Shape("<rect x=\"10\" y=\"20\" width=\"30\" height=\"40\"/>").PathData);
    }

    [Fact]
    public void Rounded_rect_uses_arcs_and_a_single_radius_applies_to_both_axes()
    {
        var data = Shape("<rect width=\"20\" height=\"10\" rx=\"2\"/>").PathData;
        Assert.Equal("M 2 0 H 18 A 2 2 0 0 1 20 2 V 8 A 2 2 0 0 1 18 10 H 2 A 2 2 0 0 1 0 8 V 2 A 2 2 0 0 1 2 0 Z", data);
    }

    [Fact]
    public void Rect_radius_is_clamped_to_half_the_side()
    {
        var data = Shape("<rect width=\"20\" height=\"10\" rx=\"50\" ry=\"50\"/>").PathData;
        Assert.StartsWith("M 10 0 H 10 A 10 5 0 0 1 20 5", data);
    }

    [Fact]
    public void Zero_or_negative_sized_shapes_are_skipped()
    {
        Assert.Empty(Parse("<rect width=\"0\" height=\"10\"/><rect width=\"-5\" height=\"10\"/><circle r=\"0\"/><ellipse rx=\"5\" ry=\"0\"/><polyline points=\"1\"/>").Items);
    }

    [Fact]
    public void Circle_and_ellipse_become_four_arcs()
    {
        Assert.Equal("M 15 10 A 5 5 0 0 1 10 15 A 5 5 0 0 1 5 10 A 5 5 0 0 1 10 5 A 5 5 0 0 1 15 10 Z",
            Shape("<circle cx=\"10\" cy=\"10\" r=\"5\"/>").PathData);
        Assert.Equal("M 8 2 A 4 2 0 0 1 4 4 A 4 2 0 0 1 0 2 A 4 2 0 0 1 4 0 A 4 2 0 0 1 8 2 Z",
            Shape("<ellipse cx=\"4\" cy=\"2\" rx=\"4\" ry=\"2\"/>").PathData);
    }

    [Fact]
    public void Line_polyline_and_polygon()
    {
        Assert.Equal("M 1 2 L 3 4", Shape("<line x1=\"1\" y1=\"2\" x2=\"3\" y2=\"4\"/>").PathData);
        Assert.Equal("M 0 0 L 10 0 L 10 10", Shape("<polyline points=\"0,0 10,0 10,10\"/>").PathData);
        Assert.Equal("M 0 0 L 10 0 L 10 0 L 10 10 Z", Shape("<polygon points=\"0 0,10 0 10-0 10 10\"/>").PathData);
    }

    [Fact]
    public void Polygon_drops_an_odd_trailing_coordinate()
    {
        Assert.Equal("M 0 0 L 10 0 L 10 10 Z", Shape("<polygon points=\"0 0 10 0 10 10 7\"/>").PathData);
    }

    [Fact]
    public void Path_data_is_kept_as_written()
    {
        Assert.Equal("m10,10 l5-5z", Shape("<path d=\"m10,10 l5-5z\"/>").PathData);
    }

    [Fact]
    public void Percent_lengths_resolve_against_the_viewport()
    {
        Assert.Equal("M 0 0 H 50 V 25 H 0 Z", Shape("<rect width=\"50%\" height=\"25%\"/>").PathData);
    }

    // ---- Path normalization ----

    [Theory]
    [InlineData("M10 10 L20 20", "M 10 10 L 20 20")]
    [InlineData("m10,10 l5-5z", "M 10 10 L 15 5 Z")]
    [InlineData("M1.5.5L2,2", "M 1.5 0.5 L 2 2")]
    [InlineData("M0 0 10 10 20 0", "M 0 0 L 10 10 L 20 0")]
    [InlineData("m1 1 2 2", "M 1 1 L 3 3")]
    [InlineData("M0 0 h10 v10 H0 V5", "M 0 0 L 10 0 L 10 10 L 0 10 L 0 5")]
    [InlineData("M0 0 c1 1 2 2 3 3 s1 1 2 2", "M 0 0 C 1 1 2 2 3 3 S 4 4 5 5")]
    [InlineData("M0 0 q1 1 2 2 t2 2", "M 0 0 Q 1 1 2 2 T 4 4")]
    [InlineData("M0 0a5 5 0 1010 0", "M 0 0 A 5 5 0 1 0 10 0")]
    [InlineData("M0 0 A0 5 0 0 1 10 0", "M 0 0 L 10 0")]
    [InlineData("M0 0 A-5 5 0 0 1 10 0", "M 0 0 A 5 5 0 0 1 10 0")]
    [InlineData("M1e1 2E-1", "M 10 0.2")]
    [InlineData("M0 0 L10 10 L20", "M 0 0 L 10 10")]
    [InlineData("M0 0 L10 10 X 5 5", "M 0 0 L 10 10")]
    [InlineData("M0 0 z m1 1 l1 0", "M 0 0 Z M 1 1 L 2 1")]
    [InlineData("L10 10", "")]
    [InlineData("garbage", "")]
    [InlineData("", "")]
    public void Path_data_normalizes_to_absolute_commands_and_stops_at_errors(string input, string expected)
    {
        Assert.Equal(expected, SvgPathData.Normalize(input));
    }

    // ---- Transforms ----

    [Fact]
    public void Transform_list_composes_right_to_left()
    {
        // translate(10,0) scale(2): the point is scaled first, then translated.
        var shape = Shape("<rect width=\"1\" height=\"1\" transform=\"translate(10,0) scale(2)\"/>");
        Point(shape.Transform, 1, 1, 12, 2);
    }

    [Fact]
    public void Group_transforms_accumulate_onto_children()
    {
        var shape = Shape("<g transform=\"translate(10 20)\"><g transform=\"scale(3)\"><rect width=\"1\" height=\"1\" transform=\"translate(1)\"/></g></g>");
        Point(shape.Transform, 0, 0, 13, 20);
        Point(shape.Transform, 1, 1, 16, 23);
    }

    [Fact]
    public void Rotate_about_a_center()
    {
        var shape = Shape("<rect width=\"1\" height=\"1\" transform=\"rotate(90 10 10)\"/>");
        Point(shape.Transform, 20, 10, 10, 20);
        Point(shape.Transform, 10, 10, 10, 10);
    }

    [Fact]
    public void Matrix_and_skew_transforms()
    {
        Point(Shape("<rect width=\"1\" height=\"1\" transform=\"matrix(1 2 3 4 5 6)\"/>").Transform, 1, 1, 9, 12);
        Point(Shape("<rect width=\"1\" height=\"1\" transform=\"skewX(45)\"/>").Transform, 0, 10, 10, 10);
        Point(Shape("<rect width=\"1\" height=\"1\" transform=\"skewY(45)\"/>").Transform, 10, 0, 10, 10);
        Point(Shape("<rect width=\"1\" height=\"1\" transform=\"scale(2,3)\"/>").Transform, 1, 1, 2, 3);
        Point(Shape("<rect width=\"1\" height=\"1\" transform=\"translate(5)\"/>").Transform, 0, 0, 5, 0);
    }

    [Fact]
    public void Malformed_transform_is_ignored()
    {
        Assert.True(Shape("<rect width=\"1\" height=\"1\" transform=\"rotate(oops)\"/>").Transform.IsIdentity);
        Assert.True(Shape("<rect width=\"1\" height=\"1\" transform=\"translate(1\"/>").Transform.IsIdentity);
    }

    [Fact]
    public void Matrix_then_composes_in_application_order()
    {
        var m = SvgMatrix.Scale(2, 2).Then(SvgMatrix.Translate(1, 0));
        Point(m, 1, 1, 3, 2);
    }

    // ---- Paints and inheritance ----

    [Fact]
    public void Default_paint_is_black_fill_and_no_stroke()
    {
        var shape = Shape("<rect width=\"1\" height=\"1\"/>");
        Assert.Equal(SvgPaint.Solid(SvgColor.Black), shape.Fill);
        Assert.True(shape.Stroke.IsNone);
        Assert.Equal(1, shape.StrokeWidth);
    }

    [Fact]
    public void Presentation_attributes_inherit_from_groups()
    {
        var shape = Shape("<g fill=\"red\" stroke=\"blue\" stroke-width=\"3\" stroke-linecap=\"round\" stroke-linejoin=\"bevel\" stroke-miterlimit=\"8\" fill-rule=\"evenodd\" fill-opacity=\"0.5\" stroke-opacity=\"25%\"><rect width=\"1\" height=\"1\"/></g>");
        Assert.Equal(SvgPaint.Solid(Rgb(255, 0, 0)), shape.Fill);
        Assert.Equal(SvgPaint.Solid(Rgb(0, 0, 255)), shape.Stroke);
        Assert.Equal(3, shape.StrokeWidth);
        Assert.Equal(SvgLineCap.Round, shape.StrokeLineCap);
        Assert.Equal(SvgLineJoin.Bevel, shape.StrokeLineJoin);
        Assert.Equal(8, shape.StrokeMiterLimit);
        Assert.Equal(SvgFillRule.EvenOdd, shape.FillRule);
        Assert.Equal(0.5, shape.FillOpacity);
        Assert.Equal(0.25, shape.StrokeOpacity);
    }

    [Fact]
    public void Opacity_is_not_inherited_but_a_translucent_group_wraps_its_children()
    {
        var document = Parse("<g opacity=\"0.5\"><rect width=\"1\" height=\"1\"/></g><rect width=\"1\" height=\"1\" opacity=\"0.3\"/>");
        var group = Assert.IsType<SvgGroup>(document.Root.Children[0]);
        Assert.Equal(0.5, group.Opacity);
        Assert.Equal(1, Assert.IsType<SvgShape>(Assert.Single(group.Children)).Opacity);
        Assert.Equal(0.3, document.Items[1].Opacity);
        Assert.Equal(2, document.Items.Count);
    }

    [Fact]
    public void Opaque_groups_are_flattened()
    {
        var document = Parse("<g><g><rect width=\"1\" height=\"1\"/></g></g>");
        Assert.IsType<SvgShape>(Assert.Single(document.Root.Children));
    }

    [Fact]
    public void Inline_style_beats_presentation_attributes_and_child_beats_parent()
    {
        var shape = Shape("<g style=\"fill: green\"><rect width=\"1\" height=\"1\" fill=\"red\" style=\"fill:blue; stroke: lime\"/></g>");
        Assert.Equal(SvgPaint.Solid(Rgb(0, 0, 255)), shape.Fill);
        Assert.Equal(SvgPaint.Solid(Rgb(0, 255, 0)), shape.Stroke);

        var inherited = Shape("<g style=\"fill: green\"><rect width=\"1\" height=\"1\" fill=\"red\"/></g>");
        Assert.Equal(SvgPaint.Solid(Rgb(255, 0, 0)), inherited.Fill);
    }

    [Fact]
    public void Style_sheet_rules_beat_attributes_and_follow_specificity()
    {
        var document = Parse(
            "<style>rect { fill: red } .a { fill: green } #x { fill: blue } rect.a { stroke: yellow } * { stroke-width: 4 }</style>" +
            "<rect id=\"x\" class=\"a\" width=\"1\" height=\"1\" fill=\"purple\"/>" +
            "<rect class=\"b a\" width=\"1\" height=\"1\"/>" +
            "<rect width=\"1\" height=\"1\" fill=\"purple\"/>" +
            "<circle class=\"a\" r=\"1\" style=\"fill: navy\"/>");
        var shapes = document.Items.Cast<SvgShape>().ToList();
        Assert.Equal(SvgPaint.Solid(Rgb(0, 0, 255)), shapes[0].Fill);
        Assert.Equal(SvgPaint.Solid(Rgb(255, 255, 0)), shapes[0].Stroke);
        Assert.Equal(SvgPaint.Solid(Rgb(0, 128, 0)), shapes[1].Fill);
        Assert.Equal(SvgPaint.Solid(Rgb(255, 0, 0)), shapes[2].Fill);
        Assert.Equal(SvgPaint.Solid(Rgb(0, 0, 128)), shapes[3].Fill);
        Assert.All(shapes, s => Assert.Equal(4, s.StrokeWidth));
    }

    [Fact]
    public void Later_rule_of_equal_specificity_wins_and_important_beats_inline()
    {
        var document = Parse(
            "<style><![CDATA[ /* comment */ .a { fill: red } .b { fill: green } .c { stroke: red !important } ]]></style>" +
            "<rect class=\"a b c\" width=\"1\" height=\"1\" style=\"stroke: blue\"/>");
        var shape = Assert.IsType<SvgShape>(Assert.Single(document.Items));
        Assert.Equal(SvgPaint.Solid(Rgb(0, 128, 0)), shape.Fill);
        Assert.Equal(SvgPaint.Solid(Rgb(255, 0, 0)), shape.Stroke);
    }

    [Fact]
    public void Unsupported_selectors_and_at_rules_are_skipped()
    {
        var shape = Shape(
            "<style>@import url(x.css); @media print { rect { fill: red } } g rect { fill: red } rect:hover { fill: red } rect[x] { fill: red } .ok { fill: lime }</style>" +
            "<g><rect class=\"ok\" width=\"1\" height=\"1\"/></g>");
        Assert.Equal(SvgPaint.Solid(Rgb(0, 255, 0)), shape.Fill);
    }

    [Fact]
    public void Inherit_keyword_takes_the_parent_value()
    {
        var shape = Shape("<g fill=\"red\"><g fill=\"blue\"><rect width=\"1\" height=\"1\" fill=\"inherit\"/></g></g>");
        Assert.Equal(SvgPaint.Solid(Rgb(0, 0, 255)), shape.Fill);
    }

    [Fact]
    public void Current_color_uses_the_color_property_and_defaults_to_black()
    {
        Assert.Equal(SvgPaint.Solid(SvgColor.Black), Shape("<rect width=\"1\" height=\"1\" fill=\"currentColor\"/>").Fill);
        var shape = Shape("<g color=\"red\" fill=\"currentColor\"><rect width=\"1\" height=\"1\" style=\"color: lime\"/></g>");
        Assert.Equal(SvgPaint.Solid(Rgb(0, 255, 0)), shape.Fill);
    }

    [Fact]
    public void Invalid_paint_keeps_the_inherited_value()
    {
        Assert.Equal(SvgPaint.Solid(Rgb(255, 0, 0)), Shape("<g fill=\"red\"><rect width=\"1\" height=\"1\" fill=\"notacolor\"/></g>").Fill);
    }

    [Fact]
    public void Dash_array_is_parsed_and_odd_lists_repeat()
    {
        var shape = Shape("<line x2=\"10\" stroke=\"black\" stroke-dasharray=\"5, 2 1\" stroke-dashoffset=\"3\"/>");
        Assert.Equal([5, 2, 1, 5, 2, 1], shape.StrokeDashArray);
        Assert.Equal(3, shape.StrokeDashOffset);
        Assert.Empty(Shape("<line x2=\"10\" stroke-dasharray=\"none\"/>").StrokeDashArray);
        Assert.Empty(Shape("<line x2=\"10\" stroke-dasharray=\"0 0\"/>").StrokeDashArray);
        Assert.Empty(Shape("<line x2=\"10\" stroke-dasharray=\"5 -1\"/>").StrokeDashArray);
    }

    [Fact]
    public void Display_none_skips_the_subtree_and_visibility_hidden_only_hides_leaves()
    {
        var document = Parse(
            "<g display=\"none\"><rect width=\"1\" height=\"1\"/></g>" +
            "<g visibility=\"hidden\"><rect width=\"1\" height=\"1\"/><rect id=\"shown\" width=\"1\" height=\"1\" visibility=\"visible\"/></g>" +
            "<rect width=\"1\" height=\"1\" style=\"display:none\"/>");
        Assert.Equal("shown", Assert.Single(document.Items).Id);
    }

    [Fact]
    public void Em_lengths_use_the_font_size()
    {
        var shape = Shape("<g font-size=\"10\"><rect width=\"2em\" height=\"1em\" stroke-width=\"0.5em\"/></g>");
        Assert.Equal("M 0 0 H 20 V 10 H 0 Z", shape.PathData);
        Assert.Equal(5, shape.StrokeWidth);
    }

    // ---- Colors ----

    [Theory]
    [InlineData("#f00", 255, 0, 0, 255)]
    [InlineData("#F008", 255, 0, 0, 136)]
    [InlineData("#00ff00", 0, 255, 0, 255)]
    [InlineData("#0000FF80", 0, 0, 255, 128)]
    [InlineData("rgb(255, 128, 0)", 255, 128, 0, 255)]
    [InlineData("rgb(100%, 50%, 0%)", 255, 128, 0, 255)]
    [InlineData("rgba(0,0,255,0.5)", 0, 0, 255, 128)]
    [InlineData("rgb(0 0 255 / 50%)", 0, 0, 255, 128)]
    [InlineData("RGB(300, -5, 0)", 255, 0, 0, 255)]
    [InlineData("hsl(0, 100%, 50%)", 255, 0, 0, 255)]
    [InlineData("hsl(120deg 100% 25%)", 0, 128, 0, 255)]
    [InlineData("hsla(240, 100%, 50%, 0.25)", 0, 0, 255, 64)]
    [InlineData("hsl(0.5turn, 100%, 50%)", 0, 255, 255, 255)]
    [InlineData("cornflowerblue", 100, 149, 237, 255)]
    [InlineData("RebeccaPurple", 102, 51, 153, 255)]
    [InlineData("transparent", 0, 0, 0, 0)]
    [InlineData(" white ", 255, 255, 255, 255)]
    public void Colors_parse(string text, int r, int g, int b, int a)
    {
        Assert.True(SvgColors.TryParse(text, out var color));
        Assert.Equal(Rgb((byte)r, (byte)g, (byte)b, (byte)a), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#ggg")]
    [InlineData("rgb(1,2)")]
    [InlineData("rgb(1,2,3")]
    [InlineData("hsl(0, 50, 50)")]
    [InlineData("notacolor")]
    [InlineData("none")]
    [InlineData("currentColor")]
    public void Invalid_colors_are_rejected(string text)
    {
        Assert.False(SvgColors.TryParse(text, out _));
    }

    [Fact]
    public void All_css_named_colors_are_known()
    {
        Assert.Equal(149, SvgColors.Names.Count); // 148 named colors plus transparent.
    }

    // ---- Gradients ----

    [Fact]
    public void Linear_gradient_with_defaults_and_stops()
    {
        var shape = Shape(
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"red\"/><stop offset=\"50%\" style=\"stop-color: blue; stop-opacity: 0.5\"/><stop offset=\"0.2\" stop-color=\"lime\"/></linearGradient></defs>" +
            "<rect width=\"10\" height=\"10\" fill=\"url(#g)\"/>");
        Assert.Equal(SvgPaintKind.Gradient, shape.Fill.Kind);
        var gradient = Assert.IsType<SvgLinearGradient>(shape.Fill.Gradient);
        Assert.Equal(SvgGradientUnits.ObjectBoundingBox, gradient.Units);
        Assert.Equal((0.0, 0.0, 1.0, 0.0), (gradient.X1, gradient.Y1, gradient.X2, gradient.Y2));
        Assert.Equal(3, gradient.Stops.Count);
        Assert.Equal(new SvgGradientStop(0, Rgb(255, 0, 0)), gradient.Stops[0]);
        Assert.Equal(new SvgGradientStop(0.5, Rgb(0, 0, 255, 128)), gradient.Stops[1]);
        Assert.Equal(0.5, gradient.Stops[2].Offset); // Offsets never go backwards.
    }

    [Fact]
    public void Gradient_defined_after_use_and_with_user_space_units_and_transform()
    {
        var shape = Shape(
            "<rect width=\"10\" height=\"10\" style=\"fill: url('#g')\"/>" +
            "<linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"50%\" y2=\"10\" gradientTransform=\"rotate(45)\" spreadMethod=\"reflect\">" +
            "<stop offset=\"0\" stop-color=\"#000\"/><stop offset=\"1\" stop-color=\"#fff\"/></linearGradient>");
        var gradient = Assert.IsType<SvgLinearGradient>(shape.Fill.Gradient);
        Assert.Equal(SvgGradientUnits.UserSpaceOnUse, gradient.Units);
        Assert.Equal(50, gradient.X2);
        Assert.Equal(10, gradient.Y2);
        Assert.Equal(SvgSpreadMethod.Reflect, gradient.SpreadMethod);
        Point(gradient.Transform, 1, 0, Math.Sqrt(0.5), Math.Sqrt(0.5));
    }

    [Fact]
    public void Radial_gradient_with_focus_and_href_inherited_stops()
    {
        var shape = Shape(
            "<linearGradient id=\"base\"><stop offset=\"0\" stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient>" +
            "<radialGradient id=\"r\" xlink:href=\"#base\" cx=\"0.4\" r=\"30%\" fx=\"0.2\"/>" +
            "<circle r=\"5\" stroke=\"url(#r)\" fill=\"none\"/>");
        Assert.True(shape.Fill.IsNone);
        var gradient = Assert.IsType<SvgRadialGradient>(shape.Stroke.Gradient);
        Assert.Equal((0.4, 0.5, 0.3, 0.2, 0.5), (gradient.Cx, gradient.Cy, gradient.R, gradient.Fx, gradient.Fy));
        Assert.Equal(2, gradient.Stops.Count);
    }

    [Fact]
    public void Gradient_href_cycle_and_missing_references_do_not_hang()
    {
        var document = Parse(
            "<linearGradient id=\"a\" href=\"#b\"/><linearGradient id=\"b\" href=\"#a\"/>" +
            "<rect width=\"1\" height=\"1\" fill=\"url(#a)\"/><rect width=\"1\" height=\"1\" fill=\"url(#missing) green\"/><rect width=\"1\" height=\"1\" fill=\"url(#missing)\"/>");
        var shapes = document.Items.Cast<SvgShape>().ToList();
        Assert.True(shapes[0].Fill.IsNone); // No stops.
        Assert.Equal(SvgPaint.Solid(Rgb(0, 128, 0)), shapes[1].Fill);
        Assert.True(shapes[2].Fill.IsNone);
    }

    [Fact]
    public void Single_stop_gradient_is_a_solid_color()
    {
        var shape = Shape("<linearGradient id=\"g\"><stop stop-color=\"red\" stop-opacity=\"0.5\"/></linearGradient><rect width=\"1\" height=\"1\" fill=\"url(#g)\"/>");
        Assert.Equal(SvgPaint.Solid(Rgb(255, 0, 0, 128)), shape.Fill);
    }

    // ---- use, defs, symbol ----

    [Fact]
    public void Defs_are_not_drawn_but_use_instantiates_them_with_offset()
    {
        var document = Parse(
            "<defs><rect id=\"r\" width=\"2\" height=\"2\" transform=\"scale(2)\"/></defs>" +
            "<use href=\"#r\" x=\"10\" y=\"5\" fill=\"red\"/><use xlink:href=\"#r\" transform=\"translate(1 1)\"/>");
        var shapes = document.Items.Cast<SvgShape>().ToList();
        Assert.Equal(2, shapes.Count);
        Point(shapes[0].Transform, 1, 1, 12, 7);
        Assert.Equal(SvgPaint.Solid(Rgb(255, 0, 0)), shapes[0].Fill); // Inherits from the use.
        Point(shapes[1].Transform, 0, 0, 1, 1);
        Assert.Equal(SvgPaint.Solid(SvgColor.Black), shapes[1].Fill);
    }

    [Fact]
    public void Use_of_a_symbol_maps_its_view_box_to_the_use_size()
    {
        var shape = Shape(
            "<symbol id=\"s\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></symbol>" +
            "<use href=\"#s\" x=\"5\" y=\"5\" width=\"20\" height=\"20\"/>");
        Point(shape.Transform, 0, 0, 5, 5);
        Point(shape.Transform, 10, 10, 25, 25);
    }

    [Fact]
    public void Symbols_are_not_drawn_directly()
    {
        Assert.Empty(Parse("<symbol id=\"s\"><rect width=\"1\" height=\"1\"/></symbol>").Items);
    }

    [Fact]
    public void Recursive_use_terminates()
    {
        var document = Parse(
            "<g id=\"loop\"><rect width=\"1\" height=\"1\"/><use href=\"#loop\"/></g>" +
            "<use id=\"self\" href=\"#self\"/><use id=\"a\" href=\"#b\"/><use id=\"b\" href=\"#a\"/>");
        Assert.Single(document.Items);
    }

    [Theory]
    [InlineData(10, 1024)]
    [InlineData(15, 32768)]
    [InlineData(40, -1)]
    public void Exponential_use_nesting_is_bounded(int levels, int expected)
    {
        var body = new System.Text.StringBuilder("<defs><rect id=\"l0\" width=\"1\" height=\"1\"/>");
        for (var i = 1; i <= levels; i++)
        {
            body.Append($"<g id=\"l{i}\"><use href=\"#l{i - 1}\"/><use href=\"#l{i - 1}\"/></g>");
        }

        body.Append($"</defs><use href=\"#l{levels}\"/>");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var document = Parse(body.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        if (expected >= 0)
        {
            Assert.Equal(expected, document.Items.Count);
        }
        else
        {
            Assert.InRange(document.Items.Count, 0, 50_000);
        }
    }

    [Fact]
    public void Nested_svg_maps_its_own_view_box()
    {
        var shape = Shape("<svg x=\"10\" y=\"10\" width=\"20\" height=\"20\" viewBox=\"0 0 2 2\"><rect width=\"1\" height=\"1\"/></svg>");
        Point(shape.Transform, 1, 1, 20, 20);
    }

    // ---- Text ----

    [Fact]
    public void Text_with_tspans_produces_runs_with_inherited_font()
    {
        var document = Parse(
            "<g font-family=\"'Segoe UI', sans-serif\" font-size=\"20\" fill=\"#123456\">" +
            "<text x=\"10 20\" y=\"30\" text-anchor=\"middle\" font-weight=\"bold\">  Hello\n   <tspan fill=\"red\" font-style=\"italic\" font-size=\"50%\" dy=\"2\">big</tspan> <tspan x=\"5\" y=\"60\">world </tspan></text></g>");
        var text = Assert.IsType<SvgText>(Assert.Single(document.Items));
        Assert.Equal(10, text.X);
        Assert.Equal(30, text.Y);
        Assert.Equal("Hello big world", text.Content);

        var runs = text.Runs;
        Assert.Equal(4, runs.Count);
        Assert.Equal("Hello ", runs[0].Text);
        Assert.Equal("Segoe UI, sans-serif", runs[0].FontFamily);
        Assert.Equal(20, runs[0].FontSize);
        Assert.Equal(700, runs[0].FontWeight);
        Assert.Equal(SvgTextAnchor.Middle, runs[0].TextAnchor);
        Assert.Equal(SvgPaint.Solid(Rgb(0x12, 0x34, 0x56)), runs[0].Fill);

        Assert.Equal("big", runs[1].Text);
        Assert.True(runs[1].Italic);
        Assert.Equal(10, runs[1].FontSize);
        Assert.Equal(2, runs[1].Dy);
        Assert.Equal(SvgPaint.Solid(Rgb(255, 0, 0)), runs[1].Fill);

        Assert.Equal(" ", runs[2].Text);
        Assert.Equal("world", runs[3].Text);
        Assert.Equal(5, runs[3].X);
        Assert.Equal(60, runs[3].Y);
    }

    [Fact]
    public void Empty_text_is_skipped()
    {
        Assert.Empty(Parse("<text x=\"1\" y=\"1\">   </text><text/>").Items);
    }

    // ---- Malformed and hostile input ----

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<svg><rect></svg>")]
    [InlineData("")]
    [InlineData("<html xmlns=\"http://www.w3.org/1999/xhtml\"/>")]
    [InlineData("<svg xmlns=\"http://example.com/other\"/>")]
    public void Non_svg_input_throws_svg_exception(string xml)
    {
        Assert.Throws<SvgException>(() => SvgDocument.Parse(xml));
    }

    [Fact]
    public void External_entities_are_never_resolved()
    {
        var secret = Path.GetTempFileName();
        File.WriteAllText(secret, "SECRET");
        try
        {
            var xml = $"<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY xxe SYSTEM \"{new Uri(secret).AbsoluteUri}\">]>" +
                      "<svg xmlns=\"http://www.w3.org/2000/svg\"><text x=\"1\" y=\"1\">&xxe;</text></svg>";
            try
            {
                var document = SvgDocument.Parse(xml);
                Assert.DoesNotContain(document.Items.OfType<SvgText>(), t => t.Content.Contains("SECRET", StringComparison.Ordinal));
            }
            catch (SvgException)
            {
                // Refusing the entity reference outright is also safe.
            }
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void Billion_laughs_is_refused_quickly()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">" +
                  "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\"><!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">]>" +
                  "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>&d;&d;&d;&d;</text></svg>";
        try
        {
            var document = SvgDocument.Parse(xml);
            Assert.All(document.Items.OfType<SvgText>(), t => Assert.True(t.Content.Length < 10_000));
        }
        catch (SvgException)
        {
        }
    }

    [Fact]
    public void A_doctype_without_entity_use_still_parses()
    {
        var document = SvgDocument.Parse(
            "<?xml version=\"1.0\"?><!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">" +
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"5\" height=\"5\"/></svg>");
        Assert.Single(document.Items);
    }

    [Fact]
    public void Garbage_attributes_and_unknown_elements_are_skipped()
    {
        var document = Parse(
            "<unknown><rect width=\"1\" height=\"1\"/></unknown>" +
            "<foreignObject><rect width=\"1\" height=\"1\"/></foreignObject>" +
            "<rect width=\"abc\" height=\"NaN\" x=\"Infinity\"/>" +
            "<rect width=\"1\" height=\"1\" fill=\"url(\" stroke=\"rgb(\" stroke-width=\"-3\" opacity=\"lots\" stroke-miterlimit=\"0.5\" font-size=\"huge\" foo=\"bar\"/>" +
            "<circle r=\"1e400\"/>" +
            "<path/>" +
            "<image href=\"x.png\" width=\"1\" height=\"1\"/>" +
            "<other:thing xmlns:other=\"urn:x\"><rect width=\"1\" height=\"1\"/></other:thing>");
        var shape = Assert.IsType<SvgShape>(Assert.Single(document.Items));
        Assert.Equal(SvgPaint.Solid(SvgColor.Black), shape.Fill);
        Assert.Equal(1, shape.StrokeWidth);
        Assert.Equal(1, shape.Opacity);
        Assert.Equal(4, shape.StrokeMiterLimit);
    }

    [Fact]
    public void Documents_without_namespace_are_accepted()
    {
        var document = SvgDocument.Parse("<svg width=\"4\" height=\"4\"><rect width=\"1\" height=\"1\"/></svg>");
        Assert.Single(document.Items);
    }

    [Fact]
    public void Very_deep_nesting_does_not_overflow()
    {
        var body = string.Concat(Enumerable.Repeat("<g>", 2000)) + "<rect width=\"1\" height=\"1\"/>" + string.Concat(Enumerable.Repeat("</g>", 2000));
        var document = Parse(body);
        Assert.Empty(document.Items); // Beyond the depth limit: skipped rather than crashing.
    }

    [Fact]
    public void Clip_paths_are_ignored_but_their_content_is_not_drawn()
    {
        var document = Parse("<clipPath id=\"c\"><rect width=\"1\" height=\"1\"/></clipPath><rect width=\"2\" height=\"2\" clip-path=\"url(#c)\"/>");
        Assert.Equal("M 0 0 H 2 V 2 H 0 Z", Assert.IsType<SvgShape>(Assert.Single(document.Items)).PathData);
    }
}
