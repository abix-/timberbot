using Xunit;
using Timberbot;

namespace Timberbot.Tests
{
    public class TimberbotJwTests
    {
        private readonly TimberbotJw _jw = new TimberbotJw(1024);

        // --- Primitives ---

        [Theory]
        [InlineData(0, "0")]
        [InlineData(1, "1")]
        [InlineData(-1, "-1")]
        [InlineData(42, "42")]
        [InlineData(int.MinValue, "-2147483648")]
        [InlineData(int.MaxValue, "2147483647")]
        public void Int_WritesValue(int input, string expected)
        {
            var result = _jw.Reset().OpenArr().Int(input).CloseArr().ToString();
            Assert.Equal($"[{expected}]", result);
        }

        [Theory]
        [InlineData(0L, "0")]
        [InlineData(1L, "1")]
        [InlineData(-1L, "-1")]
        [InlineData(long.MaxValue, "9223372036854775807")]
        public void Long_WritesValue(long input, string expected)
        {
            var result = _jw.Reset().OpenArr().Long(input).CloseArr().ToString();
            Assert.Equal($"[{expected}]", result);
        }

        [Fact]
        public void Bool_True() =>
            Assert.Equal("[true]", _jw.Reset().OpenArr().Bool(true).CloseArr().ToString());

        [Fact]
        public void Bool_False() =>
            Assert.Equal("[false]", _jw.Reset().OpenArr().Bool(false).CloseArr().ToString());

        [Fact]
        public void Str_Normal() =>
            Assert.Equal("[\"hello\"]", _jw.Reset().OpenArr().Str("hello").CloseArr().ToString());

        [Fact]
        public void Str_Empty() =>
            Assert.Equal("[\"\"]", _jw.Reset().OpenArr().Str("").CloseArr().ToString());

        [Fact]
        public void Str_Null() =>
            Assert.Equal("[\"\"]", _jw.Reset().OpenArr().Str(null).CloseArr().ToString());

        [Fact]
        public void Null_WritesNull() =>
            Assert.Equal("[null]", _jw.Reset().OpenArr().Null().CloseArr().ToString());

        // --- Float ---

        [Fact]
        public void Float_Positive_F2() =>
            Assert.Equal("[3.14]", _jw.Reset().OpenArr().Float(3.14f).CloseArr().ToString());

        [Fact]
        public void Float_Zero_F2() =>
            Assert.Equal("[0.00]", _jw.Reset().OpenArr().Float(0f).CloseArr().ToString());

        [Fact]
        public void Float_Negative_F2() =>
            Assert.Equal("[-3.14]", _jw.Reset().OpenArr().Float(-3.14f).CloseArr().ToString());

        [Fact]
        public void Float_F1() =>
            Assert.Equal("[3.1]", _jw.Reset().OpenArr().Float(3.14f, "F1").CloseArr().ToString());

        [Fact]
        public void Float_SmallFraction_F2_PadsZero() =>
            Assert.Equal("[1.05]", _jw.Reset().OpenArr().Float(1.05f).CloseArr().ToString());

        [Fact]
        public void Float_Negative_NearZero() =>
            Assert.Equal("[-0.30]", _jw.Reset().OpenArr().Float(-0.3f).CloseArr().ToString());

        // --- Structure ---

        [Fact]
        public void EmptyObject() =>
            Assert.Equal("{}", _jw.Reset().OpenObj().CloseObj().ToString());

        [Fact]
        public void EmptyArray() =>
            Assert.Equal("[]", _jw.Reset().OpenArr().CloseArr().ToString());

        [Fact]
        public void NestedObjects()
        {
            var result = _jw.Reset().OpenObj()
                .Key("outer").OpenObj()
                    .Key("inner").Int(1)
                .CloseObj()
            .CloseObj().ToString();
            Assert.Equal("{\"outer\":{\"inner\":1}}", result);
        }

        [Fact]
        public void NestedArrays()
        {
            var result = _jw.Reset().OpenArr()
                .OpenArr().Int(1).Int(2).CloseArr()
                .OpenArr().Int(3).Int(4).CloseArr()
            .CloseArr().ToString();
            Assert.Equal("[[1,2],[3,4]]", result);
        }

        [Fact]
        public void ArrayOfObjects()
        {
            var result = _jw.Reset().OpenArr()
                .OpenObj().Key("id").Int(1).Key("name").Str("Path").CloseObj()
                .OpenObj().Key("id").Int(2).Key("name").Str("Farm").CloseObj()
            .CloseArr().ToString();
            Assert.Equal("[{\"id\":1,\"name\":\"Path\"},{\"id\":2,\"name\":\"Farm\"}]", result);
        }

        [Fact]
        public void ObjectWithArrayValue()
        {
            var result = _jw.Reset().OpenObj()
                .Key("items").OpenArr().Int(1).Int(2).Int(3).CloseArr()
            .CloseObj().ToString();
            Assert.Equal("{\"items\":[1,2,3]}", result);
        }

        // --- Comma handling ---

        [Fact]
        public void MultipleKeysInObject()
        {
            var result = _jw.Reset().OpenObj()
                .Key("a").Int(1)
                .Key("b").Int(2)
                .Key("c").Int(3)
            .CloseObj().ToString();
            Assert.Equal("{\"a\":1,\"b\":2,\"c\":3}", result);
        }

        [Fact]
        public void MultipleItemsInArray()
        {
            var result = _jw.Reset().OpenArr().Int(1).Int(2).Int(3).CloseArr().ToString();
            Assert.Equal("[1,2,3]", result);
        }

        [Fact]
        public void MixedNesting()
        {
            var result = _jw.Reset().OpenObj()
                .Key("arr").OpenArr().Int(1).Int(2).CloseArr()
                .Key("obj").OpenObj().Key("x").Int(1).CloseObj()
            .CloseObj().ToString();
            Assert.Equal("{\"arr\":[1,2],\"obj\":{\"x\":1}}", result);
        }

        // --- Convenience methods ---

        [Fact]
        public void Prop_Int() =>
            Assert.Equal("{\"x\":42}", _jw.Reset().OpenObj().Prop("x", 42).CloseObj().ToString());

        [Fact]
        public void Prop_Long() =>
            Assert.Equal("{\"x\":99}", _jw.Reset().OpenObj().Prop("x", 99L).CloseObj().ToString());

        [Fact]
        public void Prop_Bool() =>
            Assert.Equal("{\"x\":true}", _jw.Reset().OpenObj().Prop("x", true).CloseObj().ToString());

        [Fact]
        public void Prop_String() =>
            Assert.Equal("{\"x\":\"hi\"}", _jw.Reset().OpenObj().Prop("x", "hi").CloseObj().ToString());

        [Fact]
        public void Prop_Float() =>
            Assert.Equal("{\"x\":1.50}", _jw.Reset().OpenObj().Prop("x", 1.5f).CloseObj().ToString());

        [Fact]
        public void Obj_Shortcut()
        {
            var result = _jw.Reset().OpenObj().Obj("inner").Key("a").Int(1).CloseObj().CloseObj().ToString();
            Assert.Equal("{\"inner\":{\"a\":1}}", result);
        }

        [Fact]
        public void Arr_Shortcut()
        {
            var result = _jw.Reset().OpenObj().Arr("items").Int(1).Int(2).CloseArr().CloseObj().ToString();
            Assert.Equal("{\"items\":[1,2]}", result);
        }

        [Fact]
        public void Raw_InjectsPrebuilt()
        {
            var result = _jw.Reset().OpenArr().Raw("{\"pre\":1}").CloseArr().ToString();
            Assert.Equal("[{\"pre\":1}]", result);
        }

        [Fact]
        public void RawProp_Shortcut()
        {
            var result = _jw.Reset().OpenObj().RawProp("data", "[1,2]").CloseObj().ToString();
            Assert.Equal("{\"data\":[1,2]}", result);
        }

        // --- Begin/End shortcuts ---

        [Fact]
        public void BeginArr_End()
        {
            var result = _jw.BeginArr().Int(1).Int(2).End();
            Assert.Equal("[1,2]", result);
        }

        [Fact]
        public void BeginObj_End()
        {
            var result = _jw.BeginObj().Key("a").Int(1).End();
            Assert.Equal("{\"a\":1}", result);
        }

        // --- One-call builders ---

        [Fact]
        public void Result_MultipleProps()
        {
            var result = _jw.Result(("id", (object)5), ("name", (object)"Path"), ("placed", (object)true));
            Assert.Equal("{\"id\":5,\"name\":\"Path\",\"placed\":true}", result);
        }

        [Fact]
        public void Error_Simple()
        {
            var result = _jw.Error("not_found");
            Assert.Equal("{\"error\":\"not_found\"}", result);
        }

        [Fact]
        public void Error_WithExtra()
        {
            var result = _jw.Error("not_found", ("id", (object)42));
            Assert.Equal("{\"error\":\"not_found\",\"id\":42}", result);
        }

        // --- Reset/reuse ---

        [Fact]
        public void Reset_ProducesCleanOutput()
        {
            _jw.Reset().OpenObj().Key("old").Int(1).CloseObj().ToString();
            var result = _jw.Reset().OpenObj().Key("new").Int(2).CloseObj().ToString();
            Assert.Equal("{\"new\":2}", result);
        }

        [Fact]
        public void ToInnerString_Object()
        {
            var result = _jw.Reset().OpenObj().Prop("a", 1).Prop("b", 2).CloseObj().ToInnerString();
            Assert.Equal("\"a\":1,\"b\":2", result);
        }

        [Fact]
        public void ToInnerString_Array()
        {
            var result = _jw.Reset().OpenArr().Int(1).Int(2).CloseArr().ToInnerString();
            Assert.Equal("1,2", result);
        }

        [Fact]
        public void ToInnerString_Empty()
        {
            var result = _jw.Reset().OpenObj().CloseObj().ToInnerString();
            Assert.Equal("", result);
        }

        // --- Complex realistic scenario ---

        [Fact]
        public void RealisticBuildingPayload()
        {
            var result = _jw.BeginArr();
            for (int i = 0; i < 3; i++)
            {
                result.OpenObj()
                    .Prop("id", i)
                    .Prop("name", "Building" + i)
                    .Prop("finished", i > 0)
                    .Prop("workers", i)
                    .Prop("priority", "Normal")
                    .Arr("inventory").CloseArr()
                .CloseObj();
            }
            var json = result.End();
            Assert.StartsWith("[{\"id\":0", json);
            Assert.EndsWith("}]", json);
            Assert.Contains(",{\"id\":1", json);
            Assert.Contains(",{\"id\":2", json);
        }
    }
}
